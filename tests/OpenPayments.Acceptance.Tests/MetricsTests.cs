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
public sealed class MetricsTests(AcceptanceFixture db)
{
    [Fact]
    public async Task Metrics_endpoint_exposes_the_contract_and_observes_a_request()
    {
        await db.ResetAsync();
        db.Psp.Next(new PspResult.Approved("psp_metrics"));
        using var client = Authorized(db, "live_key_a");

        using var payment = await PostAsync(client, "/v1/payments", "metrics-payment", new
        {
            amount_minor = 1000,
            currency = "USD",
            method = new { type = "card", @ref = "tok_test" },
            capture_mode = "manual",
        });
        Assert.Equal(HttpStatusCode.OK, payment.StatusCode);

        using var response = await client.GetAsync("/metrics");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();

        Assert.Contains("# TYPE op_requests_admitted_total counter", text);
        Assert.Contains("op_payments_completed_total{outcome=\"authorized\"} 1", text);
        Assert.Contains("# TYPE op_platform_overhead_seconds histogram", text);
        Assert.Contains("op_platform_overhead_seconds_count 1", text);
        Assert.Contains("op_psp_wait_seconds_count 1", text);
        Assert.Contains("op_inflight 0", text);
        Assert.Contains("# TYPE process_cpu_seconds_total counter", text);
        Assert.Contains("process_resident_memory_bytes", text);
        Assert.Contains("process_num_threads", text);
        Assert.Contains("op_threadpool_queue", text);
        Assert.Contains("op_gen2_gc_per_second", text);
        Assert.Contains("op_allocation_rate_bytes_per_second", text);
        Assert.Contains("op_abandoned_commits", text);
        Assert.Contains("op_commit_warnings_total", text);
        Assert.Contains("op_tx_retries_total", text);
        Assert.Contains("op_conditional_update_zero_rows_total", text);
    }

    [Fact]
    public async Task Metrics_count_a_decline_and_an_authentication_rejection()
    {
        await db.ResetAsync();
        db.Psp.Next(new PspResult.Declined("insufficient_funds"));
        using var client = Authorized(db, "live_key_a");

        using var declined = await PostAsync(client, "/v1/payments", "metrics-decline", new
        {
            amount_minor = 1000,
            currency = "USD",
            method = new { type = "card", @ref = "tok_test" },
            capture_mode = "manual",
        });
        Assert.Equal(HttpStatusCode.PaymentRequired, declined.StatusCode);

        using var anonymous = db.Client();
        using var unauthenticatedRequest = new HttpRequestMessage(
            HttpMethod.Get, "/v1/payments/00000000-0000-7000-8000-000000000001");
        using var unauthenticated = await anonymous.SendAsync(unauthenticatedRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

        using var response = await client.GetAsync("/metrics");
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("op_payments_completed_total{outcome=\"declined\"} 1", text);
        Assert.Contains("op_requests_rejected_total{reason=\"auth\"} 1", text);
    }

    [Fact]
    public async Task Standalone_capture_does_not_raise_the_completed_payment_count()
    {
        await db.ResetAsync();
        db.Psp.Next(new PspResult.Approved("psp_authorize"));
        using var client = Authorized(db, "live_key_a");

        using var created = await PostAsync(client, "/v1/payments", "metrics-capture", new
        {
            amount_minor = 1000,
            currency = "USD",
            method = new { type = "card", @ref = "tok_test" },
            capture_mode = "manual",
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var paymentId = (await ReadJsonAsync(created)).GetProperty("id").GetString();

        db.Psp.Next(new PspResult.Approved("psp_capture"));
        using var captured = await PostAsync(
            client, $"/v1/payments/{paymentId}/capture", "metrics-capture-payment", new { amount_minor = 1000 });
        Assert.Equal(HttpStatusCode.OK, captured.StatusCode);

        using var response = await client.GetAsync("/metrics");
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("op_payments_completed_total{outcome=\"authorized\"} 1", text);
        Assert.DoesNotContain("op_payments_completed_total{outcome=\"captured\"}", text);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static HttpClient Authorized(AcceptanceFixture db, string key)
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
}
