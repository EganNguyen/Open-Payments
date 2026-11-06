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
public sealed class CreateTests(AcceptanceFixture db)
{
    [Fact]
    public async Task Approved_returns_success_ledger_and_outbox()
    {
        await db.ResetAsync();
        db.Psp.Next(new PspResult.Approved("psp_1"));
        using var client = Authorized(db, "live_key_a");

        using var response = await PostAsync(client, "/v1/payments", "key-1", PaymentBody(1000));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payment = await ReadJsonAsync(response);
        Assert.Equal("AUTHORIZED", payment.GetProperty("state").GetString());
        Assert.Equal(1, await db.CountAsync("select count(*) from ledger_transactions"));
        Assert.Equal(2, await db.CountAsync("select count(*) from ledger_entries"));
        Assert.Equal(1, await db.CountAsync("select count(*) from outbox"));
        Assert.Single(db.Psp.Calls);
        Assert.Equal("authorize", db.Psp.Calls[0].Operation);
    }

    [Fact]
    public async Task Declined_returns_to_awaiting_method_then_new_key_succeeds()
    {
        await db.ResetAsync();
        db.Psp.Next(new PspResult.Declined("insufficient_funds"));
        using var client = Authorized(db, "live_key_a");

        using var declined = await PostAsync(client, "/v1/payments", "key-1", PaymentBody(1000));
        Assert.Equal(HttpStatusCode.PaymentRequired, declined.StatusCode);
        var declinedBody = await ReadJsonAsync(declined);
        Assert.Equal("AWAITING_METHOD", declinedBody.GetProperty("state").GetString());
        var paymentId = declinedBody.GetProperty("id").GetString()!;

        db.Psp.Next(new PspResult.Approved("psp_2"));
        using var retried = await PostAsync(
            client,
            $"/v1/payments/{paymentId}/attempts",
            "key-2",
            new { method = new { type = "card", @ref = "tok_2" } });

        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        Assert.Equal("AUTHORIZED", (await ReadJsonAsync(retried)).GetProperty("state").GetString());
        Assert.Equal(1, await db.CountAsync("select count(*) from payments"));
    }

    [Fact]
    public async Task E21_same_key_after_decline_replays_with_no_psp_call()
    {
        await db.ResetAsync();
        db.Psp.Next(new PspResult.Declined("insufficient_funds"));
        using var client = Authorized(db, "live_key_a");

        using var first = await PostAsync(client, "/v1/payments", "key-1", PaymentBody(1000));
        using var replay = await PostAsync(client, "/v1/payments", "key-1", PaymentBody(1000));

        Assert.Equal((int)first.StatusCode, (int)replay.StatusCode);
        using var firstJson = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        using var replayJson = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.True(JsonElement.DeepEquals(firstJson.RootElement, replayJson.RootElement));
        Assert.Single(db.Psp.Calls);
    }

    [Fact]
    public async Task C5_same_key_100_concurrent_one_payment_one_effect_identical_response()
    {
        await db.ResetAsync();
        db.Psp.Next(new PspResult.Approved("psp_1"));
        using var client = Authorized(db, "live_key_a");

        var responses = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ =>
            PostAsync(client, "/v1/payments", "same-key", PaymentBody(1000))));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        var bodies = await Task.WhenAll(responses.Select(ReadRawAsync));
        using var first = JsonDocument.Parse(bodies[0]);
        Assert.All(bodies.Skip(1), body =>
        {
            using var other = JsonDocument.Parse(body);
            Assert.True(JsonElement.DeepEquals(first.RootElement, other.RootElement));
        });
        Assert.Equal(1, await db.CountAsync("select count(*) from payments"));
        Assert.Equal(1, await db.CountAsync("select count(*) from ledger_transactions"));
        Assert.Single(db.Psp.Calls);
    }

    [Fact]
    public async Task E1_same_key_different_body_422()
    {
        await db.ResetAsync();
        db.Psp.Next(new PspResult.Approved("psp_1"));
        using var client = Authorized(db, "live_key_a");

        using var first = await PostAsync(client, "/v1/payments", "same-key", PaymentBody(1000));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var different = await PostAsync(client, "/v1/payments", "same-key", PaymentBody(2000));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, different.StatusCode);
        Assert.Single(db.Psp.Calls);
    }

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

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static Task<string> ReadRawAsync(HttpResponseMessage response) =>
        response.Content.ReadAsStringAsync();

    private static object PaymentBody(long amount) => new
    {
        amount_minor = amount,
        currency = "USD",
        method = new { type = "card", @ref = "tok_test" },
        capture_mode = "manual",
    };
}
