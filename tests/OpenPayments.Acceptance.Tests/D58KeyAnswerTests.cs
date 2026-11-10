using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using OpenPayments.Api;
using Xunit;

namespace OpenPayments.Acceptance.Tests;

/// <summary>
/// D58: a key's answer commits in transaction B with the result it describes. PostgreSQL's now()
/// is the transaction's start time, so the key's resolved_at equals the payment's updated_at only
/// when both were written in one transaction; a separate commit always gives a later time.
/// </summary>
[Collection("acceptance")]
public sealed class D58KeyAnswerTests(AcceptanceFixture db)
{
    [Fact]
    public async Task Sale_answer_commits_with_the_result_and_replays_unchanged()
    {
        await db.ResetAsync();
        using var client = Authorized();

        using var sale = await PostAsync(client, "/v1/payments", "d58-sale", new
        {
            amount_minor = 1000,
            currency = "USD",
            method = new { type = "card", @ref = "tok_d58" },
            capture_mode = "auto",
        });
        Assert.Equal(HttpStatusCode.OK, sale.StatusCode);
        var first = await sale.Content.ReadAsStringAsync();

        Assert.Equal(1, await SameTransactionAsync("d58-sale"));

        using var replay = await PostAsync(client, "/v1/payments", "d58-sale", new
        {
            amount_minor = 1000,
            currency = "USD",
            method = new { type = "card", @ref = "tok_d58" },
            capture_mode = "auto",
        });
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        // Stored answers are jsonb, which orders keys its own way; compare the JSON, not the text.
        Assert.True(JsonElement.DeepEquals(
            JsonDocument.Parse(first).RootElement,
            JsonDocument.Parse(await replay.Content.ReadAsStringAsync()).RootElement));
    }

    [Fact]
    public async Task Capture_and_refund_answers_commit_with_their_results()
    {
        await db.ResetAsync();
        using var client = Authorized();

        using var created = await PostAsync(client, "/v1/payments", "d58-auth", new
        {
            amount_minor = 1000,
            currency = "USD",
            method = new { type = "card", @ref = "tok_d58_auth" },
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.Equal(1, await SameTransactionAsync("d58-auth"));
        var paymentId = JsonDocument.Parse(await created.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetString();

        using var captured = await PostAsync(
            client, $"/v1/payments/{paymentId}/capture", "d58-capture", new { amount_minor = 1000 });
        Assert.Equal(HttpStatusCode.OK, captured.StatusCode);
        Assert.Equal(1, await SameTransactionAsync("d58-capture"));

        using var refunded = await PostAsync(
            client, $"/v1/payments/{paymentId}/refunds", "d58-refund", new { amount_minor = 400 });
        Assert.Equal(HttpStatusCode.OK, refunded.StatusCode);
        Assert.Equal(1, await SameTransactionAsync("d58-refund"));
    }

    private Task<long> SameTransactionAsync(string key) => db.CountAsync(
        $"""
        select count(*) from idempotency_keys k
          join payments p on p.id = k.payment_id
         where k.key = '{key}' and k.resolved_at is not null and k.resolved_at = p.updated_at
        """);

    private HttpClient Authorized()
    {
        var client = db.Client();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "live_key_a");
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
}
