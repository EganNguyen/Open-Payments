using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OpenPayments.Api;
using OpenPayments.Psp;
using OpenPayments.Transitions;
using Xunit;

namespace OpenPayments.Acceptance.Tests;

[Collection("acceptance")]
public sealed class Task14Tests(AcceptanceFixture db)
{
    [Fact]
    public async Task E2_same_key_in_flight_409_retry_after()
    {
        await db.ResetAsync();
        db.Psp.Delay(TimeSpan.FromSeconds(2));
        using var client = Authorized("live_key_a");

        var first = PostAsync(client, "/v1/payments", "inflight-key", Payment(1000));
        await Task.Delay(150);
        using var second = await PostAsync(client, "/v1/payments", "inflight-key", Payment(1000));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("1", second.Headers.RetryAfter?.ToString());
        using var completed = await first;
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(100_000_000_000)]
    public async Task E11_invalid_amounts_rejected(long amount)
    {
        await db.ResetAsync();
        using var client = Authorized("live_key_a");
        using var response = await PostAsync(client, "/v1/payments", "bad-amount", Payment(amount));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Theory]
    [InlineData("JPY")]
    [InlineData("KWD")]
    public async Task E12_jpy_and_kwd_stored_in_minor_units(string currency)
    {
        await db.ResetAsync();
        using var client = Authorized("live_key_a");
        using var response = await PostAsync(client, "/v1/payments", "currency-" + currency, new
        {
            amount_minor = 2400,
            currency,
            method = new { type = "card", @ref = "tok_test" },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadAsync(response);
        Assert.Equal(2400, body.GetProperty("amount_minor").GetInt64());
        Assert.Equal(currency, body.GetProperty("currency").GetString());
    }

    [Fact]
    public async Task E17_key_for_merchant_A_gets_404_on_merchant_B_payment()
    {
        await db.ResetAsync();
        using var client = Authorized("live_key_a");
        using var created = await PostAsync(client, "/v1/payments", "owner-key", Payment(1000));
        var paymentId = (await ReadAsync(created)).GetProperty("id").GetString();

        using var other = Authorized("live_key_b");
        using var attempt = await PostAsync(
            other,
            $"/v1/payments/{paymentId}/attempts",
            "other-key",
            new { method = new { type = "card", @ref = "tok_test" } });

        Assert.Equal(HttpStatusCode.NotFound, attempt.StatusCode);
    }

    [Fact]
    public async Task Over_inflight_cap_rejects_immediately_with_retry_after()
    {
        await db.ResetAsync();
        var limiter = db.Factory.Services.GetRequiredService<InFlightLimiter>();
        limiter.SetPermitLimit(1);
        db.Psp.Delay(TimeSpan.FromSeconds(2));
        using var client = Authorized("live_key_a");
        try
        {
            var first = PostAsync(client, "/v1/payments", "cap-1", Payment(1000));
            await Task.Delay(150);
            using var second = await PostAsync(client, "/v1/payments", "cap-2", Payment(1000));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, second.StatusCode);
            Assert.Equal("1", second.Headers.RetryAfter?.ToString());
            using var completed = await first;
            Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        }
        finally
        {
            limiter.SetPermitLimit(500);
        }
    }

    [Fact]
    public async Task Canonical_log_has_required_fields_and_no_card_pattern()
    {
        await db.ResetAsync();
        using var client = Authorized("live_key_a");
        using var response = await PostAsync(client, "/v1/payments", "log-key", Payment(1000));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var log = db.Factory.Services.GetRequiredService<CanonicalLog>().Last;
        Assert.NotNull(log);
        Assert.Equal(db.MerchantA.Value.ToString("D"), log.MerchantId);
        Assert.Equal("AUTHORIZED", log.StateTo);
        Assert.Equal("v1", log.RuleVersion);
        Assert.DoesNotContain("tok_test", log.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("card", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task E25_same_key_on_another_route_422()
    {
        await db.ResetAsync();
        db.Psp.Delay(TimeSpan.FromSeconds(1));
        using var client = Authorized("live_key_a");
        using var created = await PostAsync(client, "/v1/payments", "cross-key", Payment(1000));
        var paymentId = (await ReadAsync(created)).GetProperty("id").GetString();

        using var attempt = await PostAsync(
            client,
            $"/v1/payments/{paymentId}/attempts",
            "cross-key",
            new { method = new { type = "card", @ref = "tok_test" } });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, attempt.StatusCode);
    }

    [Fact]
    public async Task E26_fraud_block_402_with_no_psp_call_and_counts_toward_cap()
    {
        await db.ResetAsync();
        db.Fraud.Block("fraud_blocked");
        using var client = Authorized("live_key_a");

        using var response = await PostAsync(client, "/v1/payments", "fraud-key", Payment(1000));

        Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode);
        Assert.Empty(db.Psp.Calls);
        Assert.Equal(1, await db.CountAsync("select count(*) from payment_attempts"));
    }

    [Fact]
    public async Task E27_retry_while_unknown_renders_current_state_with_no_psp_call()
    {
        await db.ResetAsync();
        db.Psp.Timeout();
        using var client = Authorized("live_key_a");

        using var first = await PostAsync(client, "/v1/payments", "unknown-key", Payment(1000));
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        using var replay = await PostAsync(client, "/v1/payments", "unknown-key", Payment(1000));

        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        Assert.Single(db.Psp.Calls);
    }

    [Fact]
    public async Task E28_replay_skips_fraud_and_leaves_velocity_counters_alone()
    {
        await db.ResetAsync();
        using var client = Authorized("live_key_a");

        using var first = await PostAsync(client, "/v1/payments", "replay-key", Payment(1000));
        var resolved = await db.CountAsync(
            "select count(*) from idempotency_keys where key = 'replay-key' and response_code is not null");
        Assert.Equal(1, resolved);
        var checks = db.Fraud.Checks;
        using var replay = await PostAsync(client, "/v1/payments", "replay-key", Payment(1000));

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(checks, db.Fraud.Checks);
        Assert.Single(db.Psp.Calls);
    }

    [Fact]
    public async Task E29_refused_request_retried_with_same_key_is_evaluated_fresh()
    {
        await db.ResetAsync();
        using var client = Authorized("live_key_a");
        using var created = await PostAsync(client, "/v1/payments", "create-no-method", new
        {
            amount_minor = 1000,
            currency = "USD",
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var paymentId = (await ReadAsync(created)).GetProperty("id").GetString();

        using var cancel = await PostAsync(client, $"/v1/payments/{paymentId}/cancel", "cancel-key", new { });
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);

        using var refused = await PostAsync(
            client,
            $"/v1/payments/{paymentId}/attempts",
            "refused-key",
            new { method = new { type = "card", @ref = "tok_test" } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal(0, await db.CountAsync("select count(*) from idempotency_keys where key = 'refused-key'"));
    }

    [Fact]
    public async Task C43_failed_commit_of_A_answers_503_with_no_psp_call()
    {
        await db.ResetAsync();
        var store = db.Factory.Services.GetRequiredService<PaymentStore>();
        store.BeforeBegin = () => throw new CommitOutcomeUnknownException("simulated A");
        using var client = Authorized("live_key_a");
        try
        {
            using var response = await PostAsync(client, "/v1/payments", "a-fail", Payment(1000));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Empty(db.Psp.Calls);
        }
        finally
        {
            store.BeforeBegin = null;
        }
    }

    [Fact]
    public async Task C43_failed_commit_of_B_answers_202_never_a_decline()
    {
        await db.ResetAsync();
        var store = db.Factory.Services.GetRequiredService<PaymentStore>();
        store.BeforeOutboxInsert = () => throw new CommitOutcomeUnknownException("simulated B");
        using var client = Authorized("live_key_a");
        try
        {
            using var response = await PostAsync(client, "/v1/payments", "b-fail", Payment(1000));
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }
        finally
        {
            store.BeforeOutboxInsert = null;
        }
    }

    [Fact]
    public async Task Tx_B_failure_after_approval_returns_202_stays_pending_never_decline()
    {
        await db.ResetAsync();
        var store = db.Factory.Services.GetRequiredService<PaymentStore>();
        store.BeforeOutboxInsert = () => throw new InvalidOperationException("simulated B failure");
        using var client = Authorized("live_key_a");
        try
        {
            using var response = await PostAsync(client, "/v1/payments", "b-other", Payment(1000));
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var body = await ReadAsync(response);
            Assert.NotEqual("AWAITING_METHOD", body.GetProperty("state").GetString());
        }
        finally
        {
            store.BeforeOutboxInsert = null;
        }
    }

    [Fact]
    public async Task Retry_after_B_failure_calls_psp_with_same_attempt_key_only()
    {
        await db.ResetAsync();
        var store = db.Factory.Services.GetRequiredService<PaymentStore>();
        store.BeforeOutboxInsert = () => throw new InvalidOperationException("simulated B failure");
        using var client = Authorized("live_key_a");
        try
        {
            using var first = await PostAsync(client, "/v1/payments", "b-retry", Payment(1000));
            Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
            var calls = db.Psp.Calls.Count;
            using var retry = await PostAsync(client, "/v1/payments", "b-retry", Payment(1000));
            Assert.NotEqual(HttpStatusCode.OK, retry.StatusCode);
            Assert.Equal(calls, db.Psp.Calls.Count);
        }
        finally
        {
            store.BeforeOutboxInsert = null;
        }
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

    private static object Payment(long amount) => new
    {
        amount_minor = amount,
        currency = "USD",
        method = new { type = "card", @ref = "tok_test" },
    };
}
