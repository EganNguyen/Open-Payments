using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using OpenPayments.Api;
using Xunit;

namespace OpenPayments.Acceptance.Tests;

// Task 20 (D61) at the API boundary: every finishing operation's row carries the key that
// started it, and the key resolves with that answer.
[Collection("acceptance")]
public sealed class KeyResolutionAcceptanceTests(AcceptanceFixture db)
{
    [Fact]
    public async Task Attempt_capture_and_refund_rows_carry_and_resolve_their_keys()
    {
        await db.ResetAsync();
        using var client = Authorized();

        using var created = await PostAsync(client, "/v1/payments", "kres-auth", new
        {
            amount_minor = 1000,
            currency = "USD",
            method = new { type = "card", @ref = "tok_kres" },
            capture_mode = "manual",
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var paymentId = JsonDocument.Parse(await created.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetString()!;

        using var captured = await PostAsync(
            client, $"/v1/payments/{paymentId}/capture", "kres-capture", new { amount_minor = 1000 });
        Assert.Equal(HttpStatusCode.OK, captured.StatusCode);

        using var refunded = await PostAsync(
            client, $"/v1/payments/{paymentId}/refunds", "kres-refund", new { amount_minor = 400 });
        Assert.Equal(HttpStatusCode.OK, refunded.StatusCode);

        Assert.Equal(1, await CountAsync(
            "select count(*) from payment_attempts where attempt_id is not null and idempotency_key = 'kres-auth' and resolves_key"));
        Assert.Equal(1, await CountAsync(
            "select count(*) from payment_attempts where idempotency_key = 'kres-capture' and resolves_key"));
        Assert.Equal(1, await CountAsync(
            "select count(*) from refunds where idempotency_key = 'kres-refund' and resolves_key"));
        Assert.Equal(3, await CountAsync(
            "select count(*) from idempotency_keys where key in ('kres-auth','kres-capture','kres-refund') and resolved_at is not null"));
    }

    [Fact]
    public async Task Capture_replay_returns_the_stored_answer_after_the_store_resolved_it()
    {
        await db.ResetAsync();
        using var client = Authorized();
        using var created = await PostAsync(client, "/v1/payments", "kres-replay-auth", new
        {
            amount_minor = 1000,
            currency = "USD",
            method = new { type = "card", @ref = "tok_kres_replay" },
            capture_mode = "manual",
        });
        var paymentId = JsonDocument.Parse(await created.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetString()!;

        using var first = await PostAsync(
            client, $"/v1/payments/{paymentId}/capture", "kres-replay-capture", new { amount_minor = 1000 });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstBody = await first.Content.ReadAsStringAsync();

        using var replay = await PostAsync(
            client, $"/v1/payments/{paymentId}/capture", "kres-replay-capture", new { amount_minor = 1000 });
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.True(JsonElement.DeepEquals(
            JsonDocument.Parse(firstBody).RootElement,
            JsonDocument.Parse(await replay.Content.ReadAsStringAsync()).RootElement));
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(db.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

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
