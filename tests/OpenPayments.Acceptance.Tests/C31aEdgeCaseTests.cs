using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenPayments.Api;
using OpenPayments.Psp;
using Xunit;

namespace OpenPayments.Acceptance.Tests;

[Collection("acceptance")]
public sealed class C31aEdgeCaseTests(AcceptanceFixture db)
{
    [Fact]
    public async Task E5_new_key_after_timeout_creates_a_separate_payment()
    {
        await db.ResetAsync();
        db.Psp.Timeout();
        using var client = Authorized("live_key_a");

        using var first = await PostAsync(client, "/v1/payments", "e5-timeout", Payment(1000));
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var firstId = (await ReadAsync(first)).GetProperty("id").GetString();
        Assert.Equal("UNKNOWN", (await ReadAsync(first)).GetProperty("state").GetString());

        db.Psp.Timeout(false);
        using var second = await PostAsync(client, "/v1/payments", "e5-new-key", Payment(1000));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondId = (await ReadAsync(second)).GetProperty("id").GetString();

        Assert.NotEqual(firstId, secondId);
        Assert.Equal(2, await db.CountAsync("select count(*) from payments"));
    }

    [Fact]
    public async Task E16_card_data_never_appears_in_logs_or_errors()
    {
        await db.ResetAsync();
        db.Logs.Clear();
        using var client = Authorized("live_key_a");

        using var response = await PostAsync(client, "/v1/payments", "e16-card", new
        {
            amount_minor = 1000,
            currency = "USD",
            method = new { type = "card", @ref = "4242424242424242" },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("4242424242424242", body, StringComparison.Ordinal);
        Assert.DoesNotContain("cvv", body, StringComparison.OrdinalIgnoreCase);
        Assert.All(db.Logs.Lines, line =>
        {
            Assert.DoesNotContain("4242424242424242", line, StringComparison.Ordinal);
            Assert.DoesNotContain("cvv", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotMatch(new Regex(@"\b(?:\d[ -]?){13,19}\b"), line);
        });
    }

    [Fact]
    public async Task E18_two_concurrent_new_attempts_have_one_psp_effect()
    {
        await db.ResetAsync();
        using var client = Authorized("live_key_a");
        using var created = await PostAsync(client, "/v1/payments", "e18-create", new
        {
            amount_minor = 1000,
            currency = "USD",
        });
        var paymentId = (await ReadAsync(created)).GetProperty("id").GetString();

        var first = PostAsync(client, $"/v1/payments/{paymentId}/attempts", "e18-a", new
        {
            method = new { type = "card", @ref = "tok_a" },
        });
        var second = PostAsync(client, $"/v1/payments/{paymentId}/attempts", "e18-b", new
        {
            method = new { type = "card", @ref = "tok_b" },
        });
        var responses = await Task.WhenAll(first, second);

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        Assert.Single(db.Psp.Calls);
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
