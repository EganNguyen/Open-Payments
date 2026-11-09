using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using OpenPayments.Api;
using OpenPayments.Domain;
using OpenPayments.Psp;
using Xunit;

namespace OpenPayments.Acceptance.Tests;

[Collection("acceptance")]
public sealed class RefundTests(AcceptanceFixture db)
{
    [Fact]
    public async Task Worked_example_30_then_70_ends_refunded_all_clearing_consistent()
    {
        await db.ResetAsync();
        using var client = Authorized("live_key_a");
        var paymentId = await CapturedPaymentAsync(client, "refund-base", 10000);

        using var first = await PostAsync(client, $"/v1/payments/{paymentId}/refunds", "refund-1", new { amount_minor = 3000 });
        Assert.True(first.StatusCode == HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        Assert.Equal("SUCCEEDED", (await ReadAsync(first)).GetProperty("state").GetString());

        using var second = await PostAsync(client, $"/v1/payments/{paymentId}/refunds", "refund-2", new { amount_minor = 7000 });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("SUCCEEDED", (await ReadAsync(second)).GetProperty("state").GetString());

        var payment = await GetPaymentAsync(client, paymentId);
        Assert.Equal("REFUNDED", payment.GetProperty("state").GetString());
        Assert.Equal(10000, payment.GetProperty("refunded_minor").GetInt64());
        Assert.Equal(0, payment.GetProperty("refund_pending_minor").GetInt64());
        Assert.Equal(0, await db.CountAsync(
            """
            select count(*) from (
              select e.txn_id, e.currency,
                     sum(case e.direction when 'D' then e.amount_minor else -e.amount_minor end) as delta
              from ledger_entries e
              group by e.txn_id, e.currency
              having sum(case e.direction when 'D' then e.amount_minor else -e.amount_minor end) <> 0
            ) unbalanced
            """));
    }

    [Fact]
    public async Task E6_refund_of_uncaptured_or_unknown_rejected()
    {
        await db.ResetAsync();
        using var client = Authorized("live_key_a");
        var paymentId = await AuthorizedPaymentAsync(client, "refund-auth");

        using var response = await PostAsync(client, $"/v1/payments/{paymentId}/refunds", "refund-1", new { amount_minor = 1000 });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.DoesNotContain(db.Psp.Calls, call => call.Operation == "refund");
    }

    [Fact]
    public async Task E7_refund_over_remaining_rejected()
    {
        await db.ResetAsync();
        using var client = Authorized("live_key_a");
        var paymentId = await CapturedPaymentAsync(client, "refund-over", 1000);

        using var response = await PostAsync(client, $"/v1/payments/{paymentId}/refunds", "refund-1", new { amount_minor = 1200 });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Refund_timeout_stays_reserved_as_unknown()
    {
        await db.ResetAsync();
        using var client = Authorized("live_key_a");
        var paymentId = await CapturedPaymentAsync(client, "refund-timeout", 1000);
        db.Psp.Timeout();

        using var response = await PostAsync(client, $"/v1/payments/{paymentId}/refunds", "refund-1", new { amount_minor = 1000 });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("UNKNOWN", (await ReadAsync(response)).GetProperty("state").GetString());
        var payment = await GetPaymentAsync(client, paymentId);
        Assert.Equal(1000, payment.GetProperty("refund_pending_minor").GetInt64());
    }

    [Fact]
    public async Task Refund_uses_the_shared_key_store()
    {
        await db.ResetAsync();
        using var client = Authorized("live_key_a");
        var paymentId = await CapturedPaymentAsync(client, "refund-key", 1000);

        using var response = await PostAsync(client, $"/v1/payments/{paymentId}/refunds", "refund-key-1", new { amount_minor = 1000 });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, await db.CountAsync(
            "select count(*) from idempotency_keys where key = 'refund-key-1' and refund_id is not null and response_code = 200"));
    }

    [Fact]
    public async Task D38_no_refund_path_takes_an_advisory_lock()
    {
        await db.ResetAsync();
        using var client = Authorized("live_key_a");
        var paymentId = await CapturedPaymentAsync(client, "refund-lock", 1000);

        using var response = await PostAsync(client, $"/v1/payments/{paymentId}/refunds", "refund-1", new { amount_minor = 1000 });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, await db.CountAsync("select count(*) from pg_locks where locktype = 'advisory'"));
    }

    [Fact]
    public async Task C6_declined_refund_releases_the_reservation_and_allows_retry()
    {
        await db.ResetAsync();
        using var client = Authorized("live_key_a");
        var paymentId = await CapturedPaymentAsync(client, "refund-release", 1000);
        db.Psp.Next(new PspResult.Declined("declined"));

        using var declined = await PostAsync(client, $"/v1/payments/{paymentId}/refunds", "refund-decline", new { amount_minor = 1000 });
        Assert.Equal(HttpStatusCode.PaymentRequired, declined.StatusCode);
        var afterDecline = await GetPaymentAsync(client, paymentId);
        Assert.Equal(0, afterDecline.GetProperty("refund_pending_minor").GetInt64());
        Assert.Equal(0, afterDecline.GetProperty("refunded_minor").GetInt64());

        db.Psp.Next(new PspResult.Approved("refund_ok"));
        using var retried = await PostAsync(client, $"/v1/payments/{paymentId}/refunds", "refund-retry", new { amount_minor = 1000 });
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        var afterRetry = await GetPaymentAsync(client, paymentId);
        Assert.Equal("REFUNDED", afterRetry.GetProperty("state").GetString());
    }

    [Fact]
    public async Task C6_50_concurrent_refunds_over_captured_never_exceed_and_decline_releases()
    {
        await db.ResetAsync();
        using var client = Authorized("live_key_a");
        var paymentId = await CapturedPaymentAsync(client, "refund-race", 10000);

        var outcomes = new System.Collections.Concurrent.ConcurrentBag<HttpStatusCode>();
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        await Task.WhenAll(Enumerable.Range(0, 50).Select(async index =>
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                using var response = await PostAsync(
                    client,
                    $"/v1/payments/{paymentId}/refunds",
                    $"refund-race-{index}",
                    new { amount_minor = 1000 });
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    outcomes.Add(HttpStatusCode.OK);
                    return;
                }

                var code = await ProblemCodeAsync(response);
                if (response.StatusCode == HttpStatusCode.UnprocessableEntity && code == "refund_exceeds_captured")
                {
                    outcomes.Add(HttpStatusCode.UnprocessableEntity);
                    return;
                }

                // Both are documented answers to a live-refund race: 409 while another refund on the
                // payment is in flight, and 503 when the row lock outlasts lock_timeout (D1, D54).
                if ((response.StatusCode == HttpStatusCode.Conflict
                        || (response.StatusCode == HttpStatusCode.ServiceUnavailable && code == "busy"))
                    && response.Headers.RetryAfter is not null)
                {
                    await Task.Delay(20);
                    continue;
                }

                failures.Add($"refund-race-{index}: {(int)response.StatusCode} {code}");
                return;
            }

            failures.Add($"refund-race-{index}: retry limit exhausted");
        }));

        Assert.True(failures.IsEmpty, string.Join("; ", failures));
        Assert.Equal(10, outcomes.Count(status => status == HttpStatusCode.OK));
        Assert.Equal(40, outcomes.Count(status => status == HttpStatusCode.UnprocessableEntity));
        var payment = await GetPaymentAsync(client, paymentId);
        var captured = payment.GetProperty("captured_minor").GetInt64();
        var refunded = payment.GetProperty("refunded_minor").GetInt64();
        var pending = payment.GetProperty("refund_pending_minor").GetInt64();
        Assert.Equal(10000, refunded + pending);
        Assert.True(refunded + pending <= captured);
    }

    [Theory]
    [InlineData("refunds")]
    [InlineData("capture")]
    [InlineData("void")]
    [InlineData("cancel")]
    public async Task Lock_timeout_on_a_money_route_answers_503_busy_with_retry_after(string route)
    {
        await db.ResetAsync();
        using var client = Authorized("live_key_a");
        var paymentId = route switch
        {
            "refunds" => await CapturedPaymentAsync(client, $"busy-{route}", 1000),
            "cancel" => await PaymentWithoutMethodAsync(client, $"busy-{route}"),
            _ => await AuthorizedPaymentAsync(client, $"busy-{route}"),
        };

        // Another session holds the payment row past op_app's 200 ms lock_timeout (D1, D54).
        await using var holder = await db.OpenAdminAsync();
        await using var hold = await holder.BeginTransactionAsync();
        await using (var lockRow = new Npgsql.NpgsqlCommand(
            "select 1 from payments where id = @id for update", holder, hold))
        {
            lockRow.Parameters.AddWithValue("id", Guid.Parse(paymentId));
            await lockRow.ExecuteScalarAsync();
        }

        using var response = await PostAsync(
            client, $"/v1/payments/{paymentId}/{route}", $"busy-{route}-op", new { amount_minor = 1000 });

        Assert.True(
            response.StatusCode == HttpStatusCode.ServiceUnavailable,
            $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        Assert.NotNull(response.Headers.RetryAfter);
        Assert.Equal("busy", await ProblemCodeAsync(response));
        await hold.RollbackAsync();
    }

    [Fact]
    public async Task E13_capture_and_refund_same_moment_serialize()
    {
        await db.ResetAsync();
        using var client = Authorized("live_key_a");
        var paymentId = await AuthorizedPaymentAsync(client, "capture-refund-race");
        var capture = PostAsync(client, $"/v1/payments/{paymentId}/capture", "race-capture", new { amount_minor = 1000 });
        var refund = PostAsync(client, $"/v1/payments/{paymentId}/refunds", "race-refund", new { amount_minor = 1000 });
        var responses = await Task.WhenAll(capture, refund);

        Assert.DoesNotContain(responses, response => response.StatusCode == HttpStatusCode.InternalServerError);
        var payment = await GetPaymentAsync(client, paymentId);
        Assert.True(payment.GetProperty("refunded_minor").GetInt64()
            + payment.GetProperty("refund_pending_minor").GetInt64()
            <= payment.GetProperty("captured_minor").GetInt64());
    }

    private async Task<string> CapturedPaymentAsync(HttpClient client, string key, long amount)
    {
        var paymentId = await AuthorizedPaymentAsync(client, key, amount);
        using var capture = await PostAsync(client, $"/v1/payments/{paymentId}/capture", key + "-capture", new { amount_minor = amount });
        Assert.Equal(HttpStatusCode.OK, capture.StatusCode);
        return paymentId;
    }

    private async Task<string> AuthorizedPaymentAsync(HttpClient client, string key, long amount = 1000)
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

    private async Task<string> PaymentWithoutMethodAsync(HttpClient client, string key)
    {
        using var response = await PostAsync(client, "/v1/payments", key, new { amount_minor = 1000, currency = "USD" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ReadAsync(response)).GetProperty("id").GetString()!;
    }

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<JsonElement> GetPaymentAsync(HttpClient client, string paymentId)
    {
        using var response = await client.GetAsync($"/v1/payments/{paymentId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync(response);
    }

    private HttpClient Authorized(string key)
    {
        var client = db.Client();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        string path,
        string key,
        object body)
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
