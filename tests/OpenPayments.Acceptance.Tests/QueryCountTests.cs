using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using OpenPayments.Api;
using Xunit;
using Xunit.Abstractions;

namespace OpenPayments.Acceptance.Tests;

[Collection("acceptance")]
public sealed class QueryCountTests(AcceptanceFixture db, ITestOutputHelper output)
{
    [Fact]
    public async Task S3_each_endpoint_within_query_count()
    {
        await db.ResetAsync();
        using var client = Authorized();

        db.Queries.Reset();
        using var created = await PostAsync(client, "/v1/payments", "query-create", new
        {
            amount_minor = 1000,
            currency = "USD",
            method = new { type = "card", @ref = "tok_test" },
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        output.WriteLine($"S3 create commands={db.Queries.Count}");
        AssertWithin("create", db.Queries.Count, 40);
        var paymentId = (await ReadAsync(created)).GetProperty("id").GetString();

        db.Queries.Reset();
        using var read = await client.GetAsync($"/v1/payments/{paymentId}");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        output.WriteLine($"S3 read commands={db.Queries.Count}");
        AssertWithin("read", db.Queries.Count, 10);

        db.Queries.Reset();
        using var captured = await PostAsync(client, $"/v1/payments/{paymentId}/capture", "query-capture", new { amount_minor = 1000 });
        Assert.Equal(HttpStatusCode.OK, captured.StatusCode);
        output.WriteLine($"S3 capture commands={db.Queries.Count}");
        AssertWithin("capture", db.Queries.Count, 40);

        db.Queries.Reset();
        using var refunded = await PostAsync(client, $"/v1/payments/{paymentId}/refunds", "query-refund", new { amount_minor = 1000 });
        Assert.Equal(HttpStatusCode.OK, refunded.StatusCode);
        output.WriteLine($"S3 refund commands={db.Queries.Count}");
        AssertWithin("refund", db.Queries.Count, 50);
    }

    private static void AssertWithin(string endpoint, long actual, long budget) =>
        Assert.True(actual > 0 && actual <= budget, $"{endpoint} used {actual} commands, budget {budget}");

    private HttpClient Authorized()
    {
        var client = db.Client();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "live_key_a");
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
