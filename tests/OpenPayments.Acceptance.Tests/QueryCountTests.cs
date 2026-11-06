using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using OpenPayments.Api;
using OpenPayments.Psp;
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
        Assert.Equal(29, db.Queries.Count);
        var paymentId = (await ReadAsync(created)).GetProperty("id").GetString();

        db.Queries.Reset();
        using var read = await client.GetAsync($"/v1/payments/{paymentId}");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        output.WriteLine($"S3 read commands={db.Queries.Count}");
        Assert.Equal(4, db.Queries.Count);

        db.Queries.Reset();
        using var captured = await PostAsync(client, $"/v1/payments/{paymentId}/capture", "query-capture", new { amount_minor = 1000 });
        Assert.Equal(HttpStatusCode.OK, captured.StatusCode);
        output.WriteLine($"S3 capture commands={db.Queries.Count}");
        Assert.Equal(37, db.Queries.Count);

        db.Queries.Reset();
        using var refunded = await PostAsync(client, $"/v1/payments/{paymentId}/refunds", "query-refund", new { amount_minor = 1000 });
        Assert.Equal(HttpStatusCode.OK, refunded.StatusCode);
        output.WriteLine($"S3 refund commands={db.Queries.Count}");
        Assert.Equal(37, db.Queries.Count);
    }

    [Fact]
    public async Task S3_path_counts_and_growth_are_recorded()
    {
        await db.ResetAsync();
        using var client = Authorized();

        db.Queries.Reset();
        using var sale = await PostAsync(client, "/v1/payments", "measure-sale", new
        {
            amount_minor = 1000,
            currency = "USD",
            method = new { type = "card", @ref = "tok_sale" },
            capture_mode = "auto",
        });
        Assert.Equal(HttpStatusCode.OK, sale.StatusCode);
        output.WriteLine($"S3 sale commands={db.Queries.Count}");
        PrintTransactions("sale", output);
        AssertTransactions(new[] { 8, 15 });
        Assert.Equal(29, db.Queries.Count);

        var cancelId = await CreateAwaitingAsync(client, "measure-cancel");
        db.Queries.Reset();
        using var cancel = await PostAsync(client, $"/v1/payments/{cancelId}/cancel", "measure-cancel-request", new { });
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        output.WriteLine($"S3 cancel commands={db.Queries.Count}");
        PrintTransactions("cancel", output);
        AssertTransactions(new[] { 10 });
        Assert.Equal(16, db.Queries.Count);

        var voidId = await CreateManualAsync(client, "measure-void");
        db.Queries.Reset();
        using var voided = await PostAsync(client, $"/v1/payments/{voidId}/void", "measure-void-request", new { });
        Assert.Equal(HttpStatusCode.OK, voided.StatusCode);
        output.WriteLine($"S3 void commands={db.Queries.Count}");
        PrintTransactions("void", output);
        AssertTransactions(new[] { 10, 15 });
        Assert.Equal(33, db.Queries.Count);

        var awaitingId = await CreateAwaitingAsync(client, "measure-attempt");
        db.Queries.Reset();
        using var attempt = await PostAsync(client, $"/v1/payments/{awaitingId}/attempts", "measure-attempt-request", new
        {
            method = new { type = "card", @ref = "tok_attempt" },
        });
        Assert.Equal(HttpStatusCode.OK, attempt.StatusCode);
        output.WriteLine($"S3 attempt commands={db.Queries.Count}");
        PrintTransactions("attempt", output);
        AssertTransactions(new[] { 11, 15 });
        Assert.Equal(34, db.Queries.Count);

        var replayBody = new
        {
            amount_minor = 1000,
            currency = "USD",
            method = new { type = "card", @ref = "tok_replay" },
            capture_mode = "manual",
        };
        string replayId;
        using (var first = await PostAsync(client, "/v1/payments", "measure-replay", replayBody))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            replayId = (await ReadAsync(first)).GetProperty("id").GetString()!;
        }
        db.Queries.Reset();
        using var replay = await PostAsync(client, "/v1/payments", "measure-replay", replayBody);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(replayId, (await ReadAsync(replay)).GetProperty("id").GetString());
        output.WriteLine($"S3 replay commands={db.Queries.Count}");
        PrintTransactions("replay", output, allowEmpty: true);
        Assert.Equal(2, db.Queries.Count);

        var refundPayment = await CreateSaleAsync(client, "measure-refund", 100000);
        db.Psp.Next(new PspResult.Approved("refund_1"));
        using var firstRefund = await PostAsync(
            client, $"/v1/payments/{refundPayment}/refunds", "measure-refund-1", new { amount_minor = 1000 });
        Assert.Equal(HttpStatusCode.OK, firstRefund.StatusCode);
        db.Queries.Reset();
        using var firstRead = await client.GetAsync($"/v1/payments/{refundPayment}");
        Assert.Equal(HttpStatusCode.OK, firstRead.StatusCode);
        output.WriteLine($"S3 read-after-1-refund commands={db.Queries.Count}");
        PrintTransactions("read", output, allowEmpty: true);
        Assert.Equal(4, db.Queries.Count);

        db.Queries.Reset();
        using var secondRefund = await PostAsync(
            client, $"/v1/payments/{refundPayment}/refunds", "measure-refund-2", new { amount_minor = 1000 });
        Assert.Equal(HttpStatusCode.OK, secondRefund.StatusCode);
        output.WriteLine($"S3 refund-after-1-prior commands={db.Queries.Count}");
        PrintTransactions("refund", output);
        AssertTransactions(new[] { 15, 15 });
        Assert.Equal(37, db.Queries.Count);

        for (var index = 3; index <= 10; index++)
        {
            db.Psp.Next(new PspResult.Approved($"refund_{index}"));
            using var refund = await PostAsync(
                client, $"/v1/payments/{refundPayment}/refunds", $"measure-refund-{index}", new { amount_minor = 1000 });
            Assert.Equal(HttpStatusCode.OK, refund.StatusCode);
        }

        db.Queries.Reset();
        using var tenthRead = await client.GetAsync($"/v1/payments/{refundPayment}");
        Assert.Equal(HttpStatusCode.OK, tenthRead.StatusCode);
        output.WriteLine($"S3 read-after-10-refunds commands={db.Queries.Count}");
        Assert.Equal(4, db.Queries.Count);

        db.Queries.Reset();
        using var eleventhRefund = await PostAsync(
            client, $"/v1/payments/{refundPayment}/refunds", "measure-refund-11", new { amount_minor = 1000 });
        Assert.Equal(HttpStatusCode.OK, eleventhRefund.StatusCode);
        output.WriteLine($"S3 refund-after-10-prior commands={db.Queries.Count}");
        Assert.Equal(37, db.Queries.Count);

        var cappedId = await CreateAwaitingAsync(client, "measure-attempt-cap");
        for (var index = 1; index <= 9; index++)
        {
            db.Psp.Next(new PspResult.Declined("insufficient_funds"));
            using var prior = await PostAsync(
                client, $"/v1/payments/{cappedId}/attempts", $"measure-attempt-{index}", new
                {
                    method = new { type = "card", @ref = "tok_attempt" },
                });
            Assert.Equal(HttpStatusCode.PaymentRequired, prior.StatusCode);
        }

        db.Queries.Reset();
        using var tenth = await PostAsync(
            client, $"/v1/payments/{cappedId}/attempts", "measure-attempt-10", new
            {
                method = new { type = "card", @ref = "tok_attempt" },
            });
        Assert.Equal(HttpStatusCode.OK, tenth.StatusCode);
        output.WriteLine($"S3 attempt-after-9-prior commands={db.Queries.Count}");
        Assert.Equal(34, db.Queries.Count);
    }

    private void AssertTransactions(int[] expected)
    {
        var actual = db.Queries.Transactions.Select(transaction => transaction.Statements).ToArray();
        Assert.Equal(expected, actual);
    }

    private void PrintTransactions(string label, ITestOutputHelper output, bool allowEmpty = false)
    {
        var transactions = db.Queries.Transactions;
        output.WriteLine($"{label} transactions=" + string.Join(
            ",", transactions.Select(transaction => $"{transaction.Statements}/{transaction.RoundTrips}")));
        if (!allowEmpty)
        {
            Assert.NotEmpty(transactions);
        }
        Assert.All(transactions, transaction => Assert.Equal(transaction.Statements, transaction.RoundTrips));
    }

    private async Task<string> CreateManualAsync(HttpClient client, string key)
    {
        using var response = await PostAsync(client, "/v1/payments", key, new
        {
            amount_minor = 1000,
            currency = "USD",
            method = new { type = "card", @ref = "tok_manual" },
            capture_mode = "manual",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ReadAsync(response)).GetProperty("id").GetString()!;
    }

    private async Task<string> CreateAwaitingAsync(HttpClient client, string key)
    {
        using var response = await PostAsync(client, "/v1/payments", key, new
        {
            amount_minor = 1000,
            currency = "USD",
            capture_mode = "manual",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ReadAsync(response)).GetProperty("id").GetString()!;
    }

    private async Task<string> CreateSaleAsync(HttpClient client, string key, long amount)
    {
        using var response = await PostAsync(client, "/v1/payments", key, new
        {
            amount_minor = amount,
            currency = "USD",
            method = new { type = "card", @ref = "tok_sale" },
            capture_mode = "auto",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ReadAsync(response)).GetProperty("id").GetString()!;
    }


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
