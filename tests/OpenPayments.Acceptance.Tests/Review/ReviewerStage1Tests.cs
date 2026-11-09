using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OpenPayments.Api;
using OpenPayments.Psp;
using OpenPayments.Transitions;
using Xunit;
using Xunit.Abstractions;

namespace OpenPayments.Acceptance.Tests;

// Stage 1 review gate: tests written by the reviewer, not the builder.
[Collection("acceptance")]
public sealed class ReviewerStage1Tests(AcceptanceFixture db, ITestOutputHelper output)
{
    [Fact]
    public async Task R1_capture_transaction_B_lock_timeout_after_psp_capture_is_not_a_500()
    {
        await db.ResetAsync();
        using var client = Authorized("live_key_a");
        var paymentId = await AuthorizedAsync(client, "r1");
        var store = db.Factory.Services.GetRequiredService<PaymentStore>();
        var calls = 0;
        NpgsqlConnection? locker = null;
        NpgsqlTransaction? lockTx = null;
        store.BeforePaymentUpdate = () =>
        {
            // Call 1 is transaction A of the capture; call 2 is transaction B, after the PSP captured.
            if (Interlocked.Increment(ref calls) == 2)
            {
                locker = db.OpenAdminAsync().GetAwaiter().GetResult();
                lockTx = locker.BeginTransaction();
                using var command = new NpgsqlCommand(
                    "select 1 from payments where id = @id for update", locker, lockTx);
                command.Parameters.AddWithValue("id", Guid.Parse(paymentId));
                command.ExecuteScalar();
            }
        };

        int status;
        try
        {
            using var response = await PostAsync(client, $"/v1/payments/{paymentId}/capture", "r1-capture", new { amount_minor = 1000 });
            status = (int)response.StatusCode;
            output.WriteLine($"capture answered {status}, Retry-After={response.Headers.RetryAfter}");
        }
        finally
        {
            store.BeforePaymentUpdate = null;
            if (lockTx is not null) { await lockTx.RollbackAsync(); }
            if (locker is not null) { await locker.DisposeAsync(); }
        }

        Assert.Contains(db.Psp.Calls, call => call.Operation == "capture");
        output.WriteLine("attempt states: " + await ScalarAsync(
            $"select string_agg(operation || ':' || state, ',') from payment_attempts where payment_id = '{paymentId}'"));
        output.WriteLine("key resolved: " + await ScalarAsync(
            "select coalesce(response_code::text, 'null') from idempotency_keys where key = 'r1-capture'"));
        using var retry = await PostAsync(client, $"/v1/payments/{paymentId}/capture", "r1-capture", new { amount_minor = 1000 });
        output.WriteLine($"same-key retry answered {(int)retry.StatusCode}: {await retry.Content.ReadAsStringAsync()}");

        // D28: after transaction B fails, the client gets 202 and the attempt stays findable.
        Assert.NotEqual(500, status);
    }

    [Fact]
    public async Task R2_new_attempt_lock_timeout_answers_503_with_retry_after()
    {
        await db.ResetAsync();
        using var client = Authorized("live_key_a");
        using var created = await PostAsync(client, "/v1/payments", "r2-create", new { amount_minor = 1000, currency = "USD" });
        var paymentId = (await ReadAsync(created)).GetProperty("id").GetString()!;

        await using var locker = await db.OpenAdminAsync();
        await using var tx = await locker.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("select 1 from payments where id = @id for update", locker, tx))
        {
            command.Parameters.AddWithValue("id", Guid.Parse(paymentId));
            await command.ExecuteScalarAsync();
        }

        int status;
        try
        {
            using var response = await PostAsync(client, $"/v1/payments/{paymentId}/attempts", "r2-attempt",
                new { method = new { type = "card", @ref = "tok_test" } });
            status = (int)response.StatusCode;
            output.WriteLine($"attempt answered {status}, Retry-After={response.Headers.RetryAfter}");
        }
        finally
        {
            await tx.RollbackAsync();
        }

        // D1: a 55P03 returns 503 with Retry-After.
        Assert.Equal(503, status);
    }

    [Fact]
    public async Task R3_unknown_payment_key_is_not_marked_resolved()
    {
        await db.ResetAsync();
        db.Psp.Timeout();
        using var client = Authorized("live_key_a");
        using var response = await PostAsync(client, "/v1/payments", "r3", new
        {
            amount_minor = 1000,
            currency = "USD",
            method = new { type = "card", @ref = "tok_test" },
        });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("UNKNOWN", (await ReadAsync(response)).GetProperty("state").GetString());

        var resolved = await db.CountAsync("select count(*) from idempotency_keys where key = 'r3' and resolved_at is not null");
        output.WriteLine($"keys with resolved_at set while UNKNOWN: {resolved}");
        // D19: a key is never expirable while its operation is unresolved; resolved_at drives expiry.
        Assert.Equal(0, resolved);
    }

    [Fact]
    public async Task R4_two_call_auto_capture_replay_returns_the_answer_the_client_got()
    {
        await db.ResetAsync();
        db.Psp.SupportsSale = false;
        db.Psp.Next(new PspResult.Approved("auth_ok"));
        db.Psp.Next(new PspResult.Declined("capture_declined"));
        using var client = Authorized("live_key_a");
        var body = new { amount_minor = 1000, currency = "USD", capture_mode = "auto", method = new { type = "card", @ref = "tok_test" } };

        using var first = await PostAsync(client, "/v1/payments", "r4", body);
        var firstText = await first.Content.ReadAsStringAsync();
        using var replay = await PostAsync(client, "/v1/payments", "r4", body);
        var replayText = await replay.Content.ReadAsStringAsync();
        output.WriteLine($"first {(int)first.StatusCode} {firstText}");
        output.WriteLine($"replay {(int)replay.StatusCode} {replayText}");

        // D31: a replay of a finished operation returns the stored answer, i.e. what the client was told.
        Assert.Equal(first.StatusCode, replay.StatusCode);
    }

    [Fact]
    public async Task R5_application_role_cannot_update_or_delete_ledger_rows()
    {
        await db.ResetAsync();
        using var client = Authorized("live_key_a");
        await AuthorizedAsync(client, "r5");
        await using var app = new NpgsqlConnection(db.AppConnectionString);
        await app.OpenAsync();
        foreach (var sql in new[] { "update ledger_entries set amount_minor = amount_minor + 1", "delete from ledger_transactions" })
        {
            await using var command = new NpgsqlCommand(sql, app);
            var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal("42501", error.SqlState);
        }
    }

    [Fact]
    public async Task R6_concurrent_capture_and_void_one_wins_and_ledger_balances()
    {
        for (var round = 0; round < 5; round++)
        {
            await db.ResetAsync();
            db.Psp.Delay(TimeSpan.FromMilliseconds(20));
            using var client = Authorized("live_key_a");
            var paymentId = await AuthorizedAsync(client, $"r6-{round}");
            var capture = PostAsync(client, $"/v1/payments/{paymentId}/capture", $"r6c-{round}", new { amount_minor = 1000 });
            var voiding = PostAsync(client, $"/v1/payments/{paymentId}/void", $"r6v-{round}", new { });
            var answers = await Task.WhenAll(capture, voiding);
            var codes = answers.Select(a => (int)a.StatusCode).ToArray();
            output.WriteLine($"round {round}: capture {codes[0]}, void {codes[1]}, state {await db.StateAsync(Guid.Parse(paymentId))}");
            Assert.DoesNotContain(500, codes);
            Assert.Equal(1, await db.CountAsync(
                $"select count(*) from payment_attempts where payment_id = '{paymentId}' and operation in ('capture','void') and state = 'SUCCEEDED'"));
            Assert.Equal(0, await db.CountAsync(
                "select count(*) from (select txn_id from ledger_entries group by txn_id, currency having sum(case direction when 'D' then amount_minor else -amount_minor end) <> 0) u"));
            foreach (var answer in answers) { answer.Dispose(); }
        }
    }

    [Fact]
    public async Task R7_merchant_b_cannot_read_capture_or_refund_merchant_a_payment()
    {
        await db.ResetAsync();
        using var a = Authorized("live_key_a");
        using var b = Authorized("live_key_b");
        var paymentId = await AuthorizedAsync(a, "r7");
        using var read = await b.GetAsync($"/v1/payments/{paymentId}");
        using var capture = await PostAsync(b, $"/v1/payments/{paymentId}/capture", "r7-c", new { amount_minor = 1000 });
        using var refund = await PostAsync(b, $"/v1/payments/{paymentId}/refunds", "r7-r", new { amount_minor = 100 });
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, capture.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, refund.StatusCode);
        Assert.DoesNotContain(db.Psp.Calls, call => call.Operation is "capture" or "refund");
    }

    private async Task<string> ScalarAsync(string sql)
    {
        await using var connection = await db.OpenAdminAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (await command.ExecuteScalarAsync())?.ToString() ?? "null";
    }

    private static async Task<string> AuthorizedAsync(HttpClient client, string key)
    {
        using var response = await PostAsync(client, "/v1/payments", key, new
        {
            amount_minor = 1000,
            currency = "USD",
            method = new { type = "card", @ref = "tok_test" },
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ReadAsync(response)).GetProperty("id").GetString()!;
    }

    private HttpClient Authorized(string key)
    {
        var client = db.Client();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
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
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
}
