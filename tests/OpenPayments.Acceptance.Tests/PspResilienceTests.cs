using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OpenPayments.Api;
using OpenPayments.Psp.Resilience;
using Xunit;

namespace OpenPayments.Acceptance.Tests;

// Task 23: C12's 429 backoff and C51's per-account credential rejection, through the API.
[Collection("acceptance")]
public sealed class PspResilienceTests(AcceptanceFixture db)
{
    [Fact]
    public async Task C12_psp_429_backs_off_by_retry_after_with_the_same_key_and_no_duplicate_effect()
    {
        await db.ResetAsync();
        db.Psp.RateLimitOnce = true;
        using var client = Authorized();

        using var created = await PostAsync(client, "c12-key");
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        Assert.Equal(2, db.Psp.Calls.Count(call => call.Operation == "authorize"));
        Assert.Equal(1, await CountAsync(
            "select count(*) from payment_attempts where idempotency_key = 'c12-key'"));
        Assert.Equal(1, await CountAsync(
            "select count(*) from idempotency_keys where key = 'c12-key' and resolved_at is not null"));
    }

    [Fact]
    public async Task C13_psp_outage_opens_the_breaker_and_fails_fast_with_retry_after()
    {
        await db.ResetAsync();
        db.Psp.Unavailable = true;
        using var client = Authorized();

        HttpResponseMessage? last = null;
        for (var i = 0; i < 6; i++)
        {
            last?.Dispose();
            last = await PostAsync(client, "c13-" + i);
        }

        using (last)
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, last!.StatusCode);
            Assert.NotNull(last.Headers.RetryAfter);
        }

        Assert.Equal(5, db.Psp.Calls.Count(call => call.Operation == "authorize"));
    }

    [Fact]
    public async Task C51_revoked_credential_fails_that_merchant_fast_with_stable_code_and_no_unknown()
    {
        await db.ResetAsync();
        db.Psp.Unauthorized = true;
        using var client = Authorized();

        using var created = await PostAsync(client, "c51-key");
        Assert.Equal(HttpStatusCode.PaymentRequired, created.StatusCode);
        var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("AWAITING_METHOD", body.GetProperty("state").GetString());
        Assert.Equal("psp_credential_rejected", body.GetProperty("latest_attempt")
            .GetProperty("failure_code").GetString());

        Assert.Equal(1, await CountAsync(
            "select count(*) from payment_attempts where idempotency_key = 'c51-key' and state = 'FAILED' and failure_code = 'psp_credential_rejected'"));
        Assert.Equal(0, await CountAsync("select count(*) from ledger_transactions"));
        var gate = db.Factory.Services.GetRequiredService<AccountGate>();
        Assert.True(gate.AccountOpen(db.PspAccountA));
        Assert.False(gate.PspOpen("mock"));
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(db.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private HttpClient Authorized()
    {
        var client = db.Client();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "live_key_a");
        return client;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/payments")
        {
            Content = JsonContent.Create(new
            {
                amount_minor = 1000,
                currency = "USD",
                method = new { type = "card", @ref = "tok_c51" },
                capture_mode = "manual",
            }, options: ApiJson.Options),
        };
        request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request);
    }
}
