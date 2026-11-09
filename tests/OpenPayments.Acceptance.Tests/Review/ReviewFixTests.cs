using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OpenPayments.Api;
using OpenPayments.Transitions;
using Xunit;

namespace OpenPayments.Acceptance.Tests;

// The builder's tests for the Stage 1 review findings H1, M3 and M4, beside the reviewer's own.
[Collection("acceptance")]
public sealed class ReviewFixTests(AcceptanceFixture db)
{
    [Fact]
    public async Task H1_capture_transaction_B_failure_answers_202_and_a_later_retry_renders_state()
    {
        await db.ResetAsync();
        using var client = Authorized();
        var paymentId = await AuthorizedPaymentAsync(client, "h1-auth", 1000);

        int status;
        await using (var lockOnB = LockPaymentOnSecondUpdate(paymentId))
        {
            using var capture = await PostAsync(client, $"/v1/payments/{paymentId}/capture", "h1-capture", new { amount_minor = 1000 });
            status = (int)capture.StatusCode;
            Assert.Equal("AUTHORIZED", (await ReadAsync(capture)).GetProperty("state").GetString());
        }

        // D28: the PSP captured, transaction B didn't commit; the client is told 202, not 500.
        Assert.Equal(202, status);
        Assert.Equal(0, await db.CountAsync("select count(*) from idempotency_keys where key = 'h1-capture' and resolved_at is not null"));

        // E2: within the in-flight window a same-key retry answers 409 with Retry-After.
        using (var early = await PostAsync(client, $"/v1/payments/{paymentId}/capture", "h1-capture", new { amount_minor = 1000 }))
        {
            Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
            Assert.NotNull(early.Headers.RetryAfter);
        }

        // Review M4: past the window the request has ended, so a retry renders the current state
        // with no second PSP call, instead of 409 forever.
        await AgeKeyAsync("h1-capture");
        var calls = db.Psp.Calls.Count;
        using var later = await PostAsync(client, $"/v1/payments/{paymentId}/capture", "h1-capture", new { amount_minor = 1000 });
        Assert.Equal(HttpStatusCode.Accepted, later.StatusCode);
        Assert.Equal(paymentId, (await ReadAsync(later)).GetProperty("id").GetString());
        Assert.Equal(calls, db.Psp.Calls.Count);
    }

    [Fact]
    public async Task H1_refund_transaction_B_failure_answers_202_with_the_refund()
    {
        await db.ResetAsync();
        using var client = Authorized();
        var paymentId = await CapturedPaymentAsync(client, "h1r", 1000);

        await using (var lockOnB = LockPaymentOnSecondUpdate(paymentId))
        {
            using var refund = await PostAsync(client, $"/v1/payments/{paymentId}/refunds", "h1r-refund", new { amount_minor = 400 });
            Assert.Equal(HttpStatusCode.Accepted, refund.StatusCode);
            var body = await ReadAsync(refund);
            Assert.Equal(paymentId, body.GetProperty("payment_id").GetString());
            Assert.Equal(400, body.GetProperty("amount_minor").GetInt64());
        }

        Assert.Equal(0, await db.CountAsync("select count(*) from idempotency_keys where key = 'h1r-refund' and resolved_at is not null"));
        await AgeKeyAsync("h1r-refund");
        using var replay = await PostAsync(client, $"/v1/payments/{paymentId}/refunds", "h1r-refund", new { amount_minor = 400 });
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        Assert.Equal(400, (await ReadAsync(replay)).GetProperty("amount_minor").GetInt64());
    }

    [Fact]
    public async Task M3_refund_timeout_leaves_its_key_unresolved_and_a_replay_renders_the_refund()
    {
        await db.ResetAsync();
        using var client = Authorized();
        var paymentId = await CapturedPaymentAsync(client, "m3r", 1000);
        db.Psp.Timeout();

        using var first = await PostAsync(client, $"/v1/payments/{paymentId}/refunds", "m3r-refund", new { amount_minor = 1000 });
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(0, await db.CountAsync("select count(*) from idempotency_keys where key = 'm3r-refund' and resolved_at is not null"));

        // E27: the refund recorded UNKNOWN, so a replay renders it at once, with no PSP call.
        var calls = db.Psp.Calls.Count;
        using var replay = await PostAsync(client, $"/v1/payments/{paymentId}/refunds", "m3r-refund", new { amount_minor = 1000 });
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        Assert.Equal("UNKNOWN", (await ReadAsync(replay)).GetProperty("state").GetString());
        Assert.Equal(calls, db.Psp.Calls.Count);
    }

    [Fact]
    public async Task D31_replays_of_a_finished_void_and_a_full_refund_return_their_stored_answers()
    {
        await db.ResetAsync();
        using var client = Authorized();
        var voided = await AuthorizedPaymentAsync(client, "d31v", 1000);
        using var firstVoid = await PostAsync(client, $"/v1/payments/{voided}/void", "d31v-void", new { });
        var firstVoidBody = await firstVoid.Content.ReadAsStringAsync();
        using var replayVoid = await PostAsync(client, $"/v1/payments/{voided}/void", "d31v-void", new { });

        Assert.Equal(HttpStatusCode.OK, firstVoid.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replayVoid.StatusCode);
        Assert.True(JsonElement.DeepEquals(JsonDocument.Parse(firstVoidBody).RootElement, await ReadAsync(replayVoid)));

        var refunded = await CapturedPaymentAsync(client, "d31r", 1000);
        using var firstRefund = await PostAsync(client, $"/v1/payments/{refunded}/refunds", "d31r-refund", new { amount_minor = 1000 });
        var firstRefundBody = await firstRefund.Content.ReadAsStringAsync();
        using var replayRefund = await PostAsync(client, $"/v1/payments/{refunded}/refunds", "d31r-refund", new { amount_minor = 1000 });

        Assert.Equal(HttpStatusCode.OK, firstRefund.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replayRefund.StatusCode);
        Assert.True(JsonElement.DeepEquals(JsonDocument.Parse(firstRefundBody).RootElement, await ReadAsync(replayRefund)));
    }

    [Theory]
    [InlineData("capture")]
    [InlineData("refund")]
    public async Task N1_connection_loss_in_transaction_A_answers_503_and_frees_the_key(string route)
    {
        // Review N1: before any PSP call, a dropped connection is retryable: 503 with Retry-After,
        // nothing moved, and the same key can run again.
        await db.ResetAsync();
        using var client = Authorized();
        var paymentId = route == "capture"
            ? await AuthorizedPaymentAsync(client, "n1", 1000)
            : await CapturedPaymentAsync(client, "n1", 1000);
        var store = db.Factory.Services.GetRequiredService<PaymentStore>();
        var calls = 0;
        store.BeforeOutboxInsert = () =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                throw new NpgsqlException("simulated connection loss during transaction A");
            }
        };
        var pspCalls = db.Psp.Calls.Count;
        try
        {
            using var first = route == "capture"
                ? await PostAsync(client, $"/v1/payments/{paymentId}/capture", "n1-op", new { amount_minor = 1000 })
                : await PostAsync(client, $"/v1/payments/{paymentId}/refunds", "n1-op", new { amount_minor = 400 });
            Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);
            Assert.NotNull(first.Headers.RetryAfter);
            Assert.Equal(pspCalls, db.Psp.Calls.Count);

            using var again = route == "capture"
                ? await PostAsync(client, $"/v1/payments/{paymentId}/capture", "n1-op", new { amount_minor = 1000 })
                : await PostAsync(client, $"/v1/payments/{paymentId}/refunds", "n1-op", new { amount_minor = 400 });
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        }
        finally
        {
            store.BeforeOutboxInsert = null;
        }
    }

    [Theory]
    [InlineData("authorize")]
    [InlineData("capture")]
    [InlineData("refund")]
    public async Task M8_an_adapter_failure_after_sending_is_an_unknown_outcome(string route)
    {
        // Review M8, D27: once the request may have reached the PSP, any exception is
        // inconclusive: 202 and UNKNOWN, never a 500.
        await db.ResetAsync();
        using var client = Authorized();
        string? paymentId = null;
        if (route != "authorize")
        {
            paymentId = route == "capture"
                ? await AuthorizedPaymentAsync(client, "m8", 1000)
                : await CapturedPaymentAsync(client, "m8", 1000);
        }

        db.Psp.ThrowOnce(new ArgumentException("Unknown currency code 'usd'."));
        using var response = route switch
        {
            "authorize" => await PostAsync(client, "/v1/payments", "m8-op", new
            {
                amount_minor = 1000,
                currency = "USD",
                method = new { type = "card", @ref = "tok_test" },
            }),
            "capture" => await PostAsync(client, $"/v1/payments/{paymentId}/capture", "m8-op", new { amount_minor = 1000 }),
            _ => await PostAsync(client, $"/v1/payments/{paymentId}/refunds", "m8-op", new { amount_minor = 400 }),
        };

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        if (route == "capture")
        {
            // As a capture timeout does: the payment stays AUTHORIZED and the capture attempt is UNKNOWN.
            Assert.Equal("AUTHORIZED", (await ReadAsync(response)).GetProperty("state").GetString());
            Assert.Equal(1, await db.CountAsync(
                $"select count(*) from payment_attempts where payment_id = '{paymentId}' and operation = 'capture' and state = 'UNKNOWN'"));
        }
        else
        {
            Assert.Equal("UNKNOWN", (await ReadAsync(response)).GetProperty("state").GetString());
        }
    }

    [Fact]
    public async Task M9_two_call_capture_transaction_A_failure_after_the_authorization_answers_202()
    {
        // Review M9, D28: the authorization was sent, so a failure starting the capture isn't the
        // retryable 503 of a request that did nothing.
        await db.ResetAsync();
        db.Psp.SupportsSale = false;
        using var client = Authorized();
        var store = db.Factory.Services.GetRequiredService<PaymentStore>();
        var calls = 0;
        // Call 1 is the authorization's transaction B; call 2 is the capture's transaction A.
        store.BeforeOutboxInsert = () =>
        {
            if (Interlocked.Increment(ref calls) == 2)
            {
                throw new NpgsqlException("simulated connection loss starting the capture");
            }
        };
        try
        {
            using var response = await PostAsync(client, "/v1/payments", "m9", new
            {
                amount_minor = 1000,
                currency = "USD",
                capture_mode = "auto",
                method = new { type = "card", @ref = "tok_test" },
            });
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            Assert.Equal("AUTHORIZED", (await ReadAsync(response)).GetProperty("state").GetString());
        }
        finally
        {
            store.BeforeOutboxInsert = null;
        }

        // M1: a same-key replay after the window finishes the capture.
        await AgeKeyAsync("m9");
        using var replay = await PostAsync(client, "/v1/payments", "m9", new
        {
            amount_minor = 1000,
            currency = "USD",
            capture_mode = "auto",
            method = new { type = "card", @ref = "tok_test" },
        });
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal("CAPTURED", (await ReadAsync(replay)).GetProperty("state").GetString());
    }

    /// <summary>Holds the payment row from another connection when the store makes its second
    /// update, transaction B, so that B hits the role's 200 ms lock_timeout after the PSP answered.</summary>
    private Locker LockPaymentOnSecondUpdate(string paymentId)
    {
        var store = db.Factory.Services.GetRequiredService<PaymentStore>();
        var locker = new Locker(store);
        var calls = 0;
        store.BeforePaymentUpdate = () =>
        {
            if (Interlocked.Increment(ref calls) == 2)
            {
                locker.Connection = db.OpenAdminAsync().GetAwaiter().GetResult();
                locker.Transaction = locker.Connection.BeginTransaction();
                using var command = new NpgsqlCommand("select 1 from payments where id = @id for update", locker.Connection, locker.Transaction);
                command.Parameters.AddWithValue("id", Guid.Parse(paymentId));
                command.ExecuteScalar();
            }
        };
        return locker;
    }

    private async Task AgeKeyAsync(string key)
    {
        await using var connection = await db.OpenAdminAsync();
        await using var command = new NpgsqlCommand("update idempotency_keys set created_at = now() - interval '1 minute' where key = @key", connection);
        command.Parameters.AddWithValue("key", key);
        await command.ExecuteNonQueryAsync();
    }

    private HttpClient Authorized()
    {
        var client = db.Client();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "live_key_a");
        return client;
    }

    private static async Task<string> AuthorizedPaymentAsync(HttpClient client, string key, long amount)
    {
        using var response = await PostAsync(client, "/v1/payments", key, new
        {
            amount_minor = amount,
            currency = "USD",
            method = new { type = "card", @ref = "tok_test" },
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ReadAsync(response)).GetProperty("id").GetString()!;
    }

    private static async Task<string> CapturedPaymentAsync(HttpClient client, string key, long amount)
    {
        var id = await AuthorizedPaymentAsync(client, key, amount);
        using var capture = await PostAsync(client, $"/v1/payments/{id}/capture", key + "-capture", new { amount_minor = amount });
        Assert.Equal(HttpStatusCode.OK, capture.StatusCode);
        return id;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, string key, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body, options: ApiJson.Options),
        };
        request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private sealed class Locker(PaymentStore store) : IAsyncDisposable
    {
        public NpgsqlConnection? Connection { get; set; }

        public NpgsqlTransaction? Transaction { get; set; }

        public async ValueTask DisposeAsync()
        {
            store.BeforePaymentUpdate = null;
            if (Transaction is not null)
            {
                await Transaction.RollbackAsync();
                await Transaction.DisposeAsync();
            }

            if (Connection is not null)
            {
                await Connection.DisposeAsync();
            }
        }
    }
}
