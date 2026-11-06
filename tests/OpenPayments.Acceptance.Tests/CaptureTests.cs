using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OpenPayments.Api;
using OpenPayments.Domain;
using OpenPayments.Psp;
using Xunit;

namespace OpenPayments.Acceptance.Tests;

[Collection("acceptance")]
public sealed class CaptureTests(AcceptanceFixture db)
{
    [Fact]
    public async Task Capture_moves_to_captured_with_receivable_and_payable_entries()
    {
        await db.ResetAsync();
        using var client = Authorized("live_key_a");
        var paymentId = await AuthorizedPaymentAsync(client, "capture-base");

        using var response = await PostAsync(client, $"/v1/payments/{paymentId}/capture", "capture-key", new { amount_minor = 1000 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("CAPTURED", (await ReadAsync(response)).GetProperty("state").GetString());
        Assert.Equal(3, await db.CountAsync("select count(*) from ledger_transactions"));
        Assert.Equal(6, await db.CountAsync("select count(*) from ledger_entries"));
    }

    [Fact]
    public async Task Capture_uses_a_different_psp_key_than_authorize()
    {
        await db.ResetAsync();
        using var client = Authorized("live_key_a");
        var paymentId = await AuthorizedPaymentAsync(client, "capture-keys");

        using var response = await PostAsync(client, $"/v1/payments/{paymentId}/capture", "capture-key", new { amount_minor = 1000 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, db.Psp.Calls.Count);
        Assert.NotEqual(db.Psp.Calls[0].Key, db.Psp.Calls[1].Key);
    }

    [Fact]
    public async Task C36_two_captures_different_keys_one_psp_effect()
    {
        await db.ResetAsync();
        using var client = Authorized("live_key_a");
        var paymentId = await AuthorizedPaymentAsync(client, "capture-once");

        using var first = await PostAsync(client, $"/v1/payments/{paymentId}/capture", "capture-1", new { amount_minor = 1000 });
        using var second = await PostAsync(client, $"/v1/payments/{paymentId}/capture", "capture-2", new { amount_minor = 1000 });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
        Assert.Single(db.Psp.Calls, call => call.Operation == "capture");
    }

    [Fact]
    public async Task Declined_capture_stays_authorized_and_allows_new_capture()
    {
        await db.ResetAsync();
        using var client = Authorized("live_key_a");
        var paymentId = await AuthorizedPaymentAsync(client, "capture-decline");
        db.Psp.Next(new PspResult.Declined("capture_declined"));

        using var declined = await PostAsync(client, $"/v1/payments/{paymentId}/capture", "capture-1", new { amount_minor = 1000 });
        Assert.Equal(HttpStatusCode.PaymentRequired, declined.StatusCode);
        Assert.Equal("AUTHORIZED", (await ReadAsync(declined)).GetProperty("state").GetString());

        db.Psp.Next(new PspResult.Approved("capture_ok"));
        using var retried = await PostAsync(client, $"/v1/payments/{paymentId}/capture", "capture-2", new { amount_minor = 1000 });
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        Assert.Equal("CAPTURED", (await ReadAsync(retried)).GetProperty("state").GetString());
    }

    [Fact]
    public async Task Capture_timeout_202_stays_authorized_attempt_unknown()
    {
        await db.ResetAsync();
        using var client = Authorized("live_key_a");
        var paymentId = await AuthorizedPaymentAsync(client, "capture-timeout");
        db.Psp.Timeout();

        using var response = await PostAsync(client, $"/v1/payments/{paymentId}/capture", "capture-1", new { amount_minor = 1000 });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("AUTHORIZED", (await ReadAsync(response)).GetProperty("state").GetString());
    }

    [Fact]
    public async Task D29_auto_capture_uses_one_psp_call_where_supported()
    {
        await db.ResetAsync();
        using var client = Authorized("live_key_a");
        using var response = await PostAsync(client, "/v1/payments", "sale-key", new
        {
            amount_minor = 1000,
            currency = "USD",
            method = new { type = "card", @ref = "tok_test" },
            capture_mode = "auto",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("CAPTURED", (await ReadAsync(response)).GetProperty("state").GetString());
        Assert.Single(db.Psp.Calls);
        Assert.Equal("sale", db.Psp.Calls[0].Operation);
    }

    [Fact]
    public async Task D29_auto_capture_uses_two_calls_where_unsupported()
    {
        await db.ResetAsync();
        db.Psp.SupportsSale = false;
        using var client = Authorized("live_key_a");
        try
        {
            using var response = await PostAsync(client, "/v1/payments", "auto-two", new
            {
                amount_minor = 1000,
                currency = "USD",
                method = new { type = "card", @ref = "tok_test" },
                capture_mode = "auto",
            });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("CAPTURED", (await ReadAsync(response)).GetProperty("state").GetString());
            Assert.Equal(2, db.Psp.Calls.Count);
            Assert.Equal("authorize", db.Psp.Calls[0].Operation);
            Assert.Equal("capture", db.Psp.Calls[1].Operation);
        }
        finally
        {
            db.Psp.SupportsSale = true;
        }
    }

    [Fact]
    public async Task Auto_capture_success_counts_one_completed_payment()
    {
        await db.ResetAsync();
        using var client = Authorized("live_key_a");
        using var response = await PostAsync(client, "/v1/payments", "auto-count", new
        {
            amount_minor = 1000,
            currency = "USD",
            method = new { type = "card", @ref = "tok_test" },
            capture_mode = "auto",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("CAPTURED", (await ReadAsync(response)).GetProperty("state").GetString());
        Assert.Equal(1, db.Factory.Services.GetRequiredService<OpenPayments.Api.PaymentMetrics>().CompletedPayments);
    }

    [Fact]
    public async Task D29_sale_timeout_answers_202_and_blocks_a_second_attempt()
    {
        await db.ResetAsync();
        db.Psp.Timeout();
        using var client = Authorized("live_key_a");
        using var response = await PostAsync(client, "/v1/payments", "sale-timeout", new
        {
            amount_minor = 1000,
            currency = "USD",
            method = new { type = "card", @ref = "tok_test" },
            capture_mode = "auto",
        });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var paymentId = (await ReadAsync(response)).GetProperty("id").GetString();

        using var second = await PostAsync(
            client,
            $"/v1/payments/{paymentId}/attempts",
            "sale-second",
            new { method = new { type = "card", @ref = "tok_test" } });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task E8_capture_from_non_authorized_rejected()
    {
        await db.ResetAsync();
        using var client = Authorized("live_key_a");
        using var created = await PostAsync(client, "/v1/payments", "no-method", new
        {
            amount_minor = 1000,
            currency = "USD",
        });
        var paymentId = (await ReadAsync(created)).GetProperty("id").GetString();

        using var capture = await PostAsync(client, $"/v1/payments/{paymentId}/capture", "capture-1", new { amount_minor = 1000 });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, capture.StatusCode);
    }

    [Fact]
    public async Task Void_moves_to_voided_and_clears_hold_with_own_psp_key()
    {
        await db.ResetAsync();
        using var client = Authorized("live_key_a");
        var paymentId = await AuthorizedPaymentAsync(client, "void-base");

        using var response = await PostAsync(client, $"/v1/payments/{paymentId}/void", "void-key", new { });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("VOIDED", (await ReadAsync(response)).GetProperty("state").GetString());
        Assert.Equal(2, db.Psp.Calls.Count);
        Assert.NotEqual(db.Psp.Calls[0].Key, db.Psp.Calls[1].Key);
    }

    [Fact]
    public async Task Void_timeout_202_stays_authorized_attempt_unknown()
    {
        await db.ResetAsync();
        using var client = Authorized("live_key_a");
        var paymentId = await AuthorizedPaymentAsync(client, "void-timeout");
        db.Psp.Timeout();

        using var response = await PostAsync(client, $"/v1/payments/{paymentId}/void", "void-1", new { });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("AUTHORIZED", (await ReadAsync(response)).GetProperty("state").GetString());
    }

    [Fact]
    public async Task E8_capture_after_voided_rejected()
    {
        await db.ResetAsync();
        using var client = Authorized("live_key_a");
        var paymentId = await AuthorizedPaymentAsync(client, "void-then-capture");
        using var voided = await PostAsync(client, $"/v1/payments/{paymentId}/void", "void-1", new { });
        Assert.Equal(HttpStatusCode.OK, voided.StatusCode);

        using var capture = await PostAsync(client, $"/v1/payments/{paymentId}/capture", "capture-1", new { amount_minor = 1000 });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, capture.StatusCode);
    }

    [Fact]
    public async Task E3_client_disconnect_after_A_still_completes_and_retry_returns_result()
    {
        await db.ResetAsync();
        db.Psp.Delay(TimeSpan.FromMilliseconds(500));
        using var client = Authorized("live_key_a");
        using var cancellation = new CancellationTokenSource(100);
        using (var aborted = new HttpRequestMessage(HttpMethod.Post, "/v1/payments"))
        {
            aborted.Content = JsonContent.Create(new
            {
                amount_minor = 1000,
                currency = "USD",
                method = new { type = "card", @ref = "tok_test" },
            }, options: ApiJson.Options);
            aborted.Headers.Add("Idempotency-Key", "disconnect-key");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => client.SendAsync(aborted, cancellation.Token));
        }

        HttpResponseMessage? retried = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await Task.Delay(100);
            retried = await PostAsync(client, "/v1/payments", "disconnect-key", new
            {
                amount_minor = 1000,
                currency = "USD",
                method = new { type = "card", @ref = "tok_test" },
            });
            if (retried.StatusCode == HttpStatusCode.OK)
            {
                break;
            }
        }

        Assert.NotNull(retried);
        Assert.Equal(HttpStatusCode.OK, retried!.StatusCode);
        Assert.Equal("AUTHORIZED", (await ReadAsync(retried)).GetProperty("state").GetString());
        Assert.Single(db.Psp.Calls);
    }

    [Fact]
    public async Task C50_capture_and_void_at_once_one_psp_effect()
    {
        await db.ResetAsync();
        using var client = Authorized("live_key_a");
        var paymentId = await AuthorizedPaymentAsync(client, "capture-void-race");

        var capture = PostAsync(client, $"/v1/payments/{paymentId}/capture", "race-capture", new { amount_minor = 1000 });
        var voided = PostAsync(client, $"/v1/payments/{paymentId}/void", "race-void", new { });
        var responses = await Task.WhenAll(capture, voided);

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(db.Psp.Calls, call => call.Operation is "capture" or "void");
    }

    [Fact]
    public async Task Two_call_capture_failure_leaves_authorized_and_reports_it()
    {
        await db.ResetAsync();
        db.Psp.SupportsSale = false;
        db.Psp.Next(new PspResult.Approved("auth"));
        db.Psp.Next(new PspResult.Declined("capture_declined"));
        using var client = Authorized("live_key_a");
        try
        {
            using var response = await PostAsync(client, "/v1/payments", "two-call-decline", new
            {
                amount_minor = 1000,
                currency = "USD",
                method = new { type = "card", @ref = "tok_test" },
                capture_mode = "auto",
            });

            Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode);
            Assert.Equal("AUTHORIZED", (await ReadAsync(response)).GetProperty("state").GetString());
            Assert.Equal(2, db.Psp.Calls.Count);
        }
        finally
        {
            db.Psp.SupportsSale = true;
        }
    }

    private async Task<string> AuthorizedPaymentAsync(HttpClient client, string key)
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
