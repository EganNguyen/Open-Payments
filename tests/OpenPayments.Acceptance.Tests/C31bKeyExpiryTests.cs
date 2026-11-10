using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using OpenPayments.Api;
using OpenPayments.Transitions;
using Xunit;

namespace OpenPayments.Acceptance.Tests;

// C31b/C41: a key whose payment is UNKNOWN is never expired, however old it is; retrying it
// returns the current state and makes no second charge. Expiry removes only resolved keys.
[Collection("acceptance")]
public sealed class C31bKeyExpiryTests(AcceptanceFixture db)
{
    [Fact]
    public async Task C41_unknown_payment_past_24h_key_kept_retry_returns_state_no_second_charge()
    {
        await db.ResetAsync();
        using var client = db.Client();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "live_key_a");
        db.Psp.Timeout();
        using var created = await PostAsync(client, "c41-unknown", new
        {
            amount_minor = 1000,
            currency = "USD",
            method = new { type = "card", @ref = "tok_test" },
        });
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        var id = (await ReadAsync(created)).GetProperty("id").GetString()!;
        Assert.Equal("UNKNOWN", await db.StateAsync(Guid.Parse(id)));
        var calls = db.Psp.Calls.Count;

        db.Psp.Timeout(false);
        await using (var connection = await db.OpenAdminAsync())
        await using (var command = new Npgsql.NpgsqlCommand(
            "update idempotency_keys set created_at = now() - interval '25 hours' where key = 'c41-unknown'",
            connection))
        {
            await command.ExecuteNonQueryAsync();
        }

        using var retried = await PostAsync(client, "c41-unknown", new
        {
            amount_minor = 1000,
            currency = "USD",
            method = new { type = "card", @ref = "tok_test" },
        });

        Assert.Equal(HttpStatusCode.Accepted, retried.StatusCode);
        Assert.Equal(id, (await ReadAsync(retried)).GetProperty("id").GetString());
        Assert.Equal("UNKNOWN", await db.StateAsync(Guid.Parse(id)));
        Assert.Equal(calls, db.Psp.Calls.Count);
        Assert.Equal(0, await db.CountAsync(
            "select count(*) from idempotency_keys where key = 'c41-unknown' and resolved_at is not null"));
    }

    [Fact]
    public async Task E4_resolved_key_expiry_then_reuse_creates_a_new_operation()
    {
        await db.ResetAsync();
        using var client = db.Client();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "live_key_a");
        using var created = await PostAsync(client, "e4-reuse", new
        {
            amount_minor = 1000,
            currency = "USD",
            method = new { type = "card", @ref = "tok_test" },
            capture_mode = "auto",
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var first = (await ReadAsync(created)).GetProperty("id").GetString()!;
        Assert.Equal("CAPTURED", await db.StateAsync(Guid.Parse(first)));
        Assert.Equal(1, await db.CountAsync(
            "select count(*) from idempotency_keys where key = 'e4-reuse' and resolved_at is not null"));

        // Expiry is the reconciler's job; the key's removal is what makes reuse a new operation.
        // The worker's own delete statement is proved in ExpiryTests.
        await using (var connection = await db.OpenAdminAsync())
        await using (var command = new Npgsql.NpgsqlCommand(
            "delete from idempotency_keys where key = 'e4-reuse'", connection))
        {
            await command.ExecuteNonQueryAsync();
        }

        using var reused = await PostAsync(client, "e4-reuse", new
        {
            amount_minor = 2000,
            currency = "USD",
            method = new { type = "card", @ref = "tok_test" },
            capture_mode = "auto",
        });

        Assert.Equal(HttpStatusCode.OK, reused.StatusCode);
        var second = (await ReadAsync(reused)).GetProperty("id").GetString()!;
        Assert.NotEqual(first, second);
        Assert.Equal(2, db.Psp.Calls.Count);
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string key, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/payments")
        {
            Content = JsonContent.Create(body, options: ApiJson.Options),
        };
        request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
}
