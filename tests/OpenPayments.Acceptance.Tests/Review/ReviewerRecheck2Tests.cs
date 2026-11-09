using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OpenPayments.Api;
using OpenPayments.Transitions;
using Xunit;
using Xunit.Abstractions;

namespace OpenPayments.Acceptance.Tests;

// Stage 1 review, second re-check: written by the reviewer, not the builder.
[Collection("acceptance")]
public sealed class ReviewerRecheck2Tests(AcceptanceFixture db, ITestOutputHelper output)
{
    [Theory]
    [InlineData("unknown_commit")]
    [InlineData("connection_loss")]
    public async Task R9_transaction_B_failure_after_a_psp_timeout_on_create_answers_202(string failure)
    {
        await db.ResetAsync();
        db.Psp.Timeout();
        using var client = db.Client();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "live_key_a");
        var store = db.Factory.Services.GetRequiredService<PaymentStore>();
        // Create's transaction A doesn't reach the failpoint; the timeout's transaction B does.
        store.BeforeOutboxInsert = () => throw (failure == "unknown_commit"
            ? new CommitOutcomeUnknownException("simulated B")
            : new NpgsqlException("simulated connection loss during transaction B"));
        int status;
        string body;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/payments")
            {
                Content = JsonContent.Create(new
                {
                    amount_minor = 1000,
                    currency = "USD",
                    method = new { type = "card", @ref = "tok_test" },
                }, options: ApiJson.Options),
            };
            request.Headers.Add("Idempotency-Key", "r9-" + failure);
            using var response = await client.SendAsync(request);
            status = (int)response.StatusCode;
            body = await response.Content.ReadAsStringAsync();
            output.WriteLine($"{failure}: {status} {body}");
        }
        finally
        {
            store.BeforeOutboxInsert = null;
        }

        Assert.Single(db.Psp.Calls);
        // D28: after the PSP call, a failed transaction B is unknown to the client: 202, never 503.
        Assert.Equal(202, status);
    }
}
