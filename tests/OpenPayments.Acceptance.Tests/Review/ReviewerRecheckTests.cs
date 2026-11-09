using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OpenPayments.Api;
using OpenPayments.Transitions;
using Xunit;
using Xunit.Abstractions;

namespace OpenPayments.Acceptance.Tests;

// Stage 1 review re-check: written by the reviewer, not the builder.
[Collection("acceptance")]
public sealed class ReviewerRecheckTests(AcceptanceFixture db, ITestOutputHelper output)
{
    [Theory]
    [InlineData("capture")]
    [InlineData("refund")]
    public async Task R8_connection_loss_in_transaction_B_after_the_psp_is_not_a_500(string route)
    {
        await db.ResetAsync();
        using var client = db.Client();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "live_key_a");
        using var created = await PostAsync(client, "/v1/payments", "r8", new
        {
            amount_minor = 1000,
            currency = "USD",
            method = new { type = "card", @ref = "tok_test" },
        });
        var paymentId = System.Text.Json.JsonDocument.Parse(await created.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetString();
        if (route == "refund")
        {
            using var capture = await PostAsync(client, $"/v1/payments/{paymentId}/capture", "r8-c", new { amount_minor = 1000 });
            Assert.Equal(HttpStatusCode.OK, capture.StatusCode);
        }

        var store = db.Factory.Services.GetRequiredService<PaymentStore>();
        var calls = 0;
        // Call 1 is transaction A; call 2 is transaction B, after the PSP answered.
        store.BeforeOutboxInsert = () =>
        {
            if (Interlocked.Increment(ref calls) == 2)
            {
                throw new NpgsqlException("simulated connection loss during transaction B");
            }
        };
        int status;
        try
        {
            using var response = route == "capture"
                ? await PostAsync(client, $"/v1/payments/{paymentId}/capture", "r8-op", new { amount_minor = 1000 })
                : await PostAsync(client, $"/v1/payments/{paymentId}/refunds", "r8-op", new { amount_minor = 400 });
            status = (int)response.StatusCode;
            output.WriteLine($"{route} answered {status}");
        }
        finally
        {
            store.BeforeOutboxInsert = null;
        }

        // D28: any failed transaction B after the PSP call is unknown to the client: 202, not 500.
        Assert.NotEqual(500, status);
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
