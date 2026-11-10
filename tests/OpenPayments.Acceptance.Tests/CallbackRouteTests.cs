using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using Xunit;

namespace OpenPayments.Acceptance.Tests;

// Task 25, the HTTP edge: signature first, then one transaction; a bad signature writes nothing.
[Collection("acceptance")]
public sealed class CallbackRouteTests(AcceptanceFixture db)
{
    private const string Secret = "whsec_test";

    [Fact]
    public async Task Bad_signature_is_401_and_writes_nothing()
    {
        await db.ResetAsync();
        using var client = db.Client();
        var body = """{"event_id":"evt_bad","type":"authorize.approved","reference":"00000000-0000-0000-0000-000000000001"}""";

        using var response = await PostAsync(client, db.PspAccountA, body, "t=1,v1=deadbeef");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, await CountAsync("select count(*) from psp_events"));
    }

    [Fact]
    public async Task Unknown_account_is_404_and_writes_nothing()
    {
        await db.ResetAsync();
        using var client = db.Client();
        var body = """{"event_id":"evt_unknown","type":"authorize.approved","reference":"00000000-0000-0000-0000-000000000001"}""";

        using var response = await PostAsync(client, Guid.NewGuid(), body, Sign(body, DateTimeOffset.UtcNow));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, await CountAsync("select count(*) from psp_events"));
    }

    [Fact]
    public async Task Signed_event_for_an_unknown_reference_is_accepted_and_parked()
    {
        await db.ResetAsync();
        using var client = db.Client();
        var body = """{"event_id":"evt_parked","type":"authorize.approved","reference":"00000000-0000-0000-0000-000000000002","amount_minor":100,"currency":"USD"}""";

        using var response = await PostAsync(client, db.PspAccountA, body, Sign(body, DateTimeOffset.UtcNow));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, await CountAsync(
            "select count(*) from psp_events where psp_event_id = 'evt_parked' and processed_at is null"));

        // Duplicate delivery is 200 and changes nothing.
        using var duplicate = await PostAsync(client, db.PspAccountA, body, Sign(body, DateTimeOffset.UtcNow));
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        Assert.Equal(1, await CountAsync("select count(*) from psp_events"));
    }

    [Fact]
    public async Task Body_over_64kb_is_413()
    {
        await db.ResetAsync();
        using var client = db.Client();
        var body = System.Text.Json.JsonSerializer.Serialize(new
        {
            event_id = "evt_big",
            type = "authorize.approved",
            reference = "00000000-0000-0000-0000-000000000003",
            padding = new string('x', 70 * 1024),
        });

        using var response = await PostAsync(client, db.PspAccountA, body, Sign(body, DateTimeOffset.UtcNow));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0, await CountAsync("select count(*) from psp_events"));
    }

    [Fact]
    public async Task Metrics_expose_event_outcomes_and_parked_state()
    {
        await db.ResetAsync();
        using var client = db.Client();
        var body = System.Text.Json.JsonSerializer.Serialize(new
        {
            event_id = "evt_metric",
            type = "authorize.approved",
            reference = "00000000-0000-0000-0000-000000000004",
        });
        using var posted = await PostAsync(client, db.PspAccountA, body, Sign(body, DateTimeOffset.UtcNow));
        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);

        var metrics = await client.GetStringAsync("/metrics");
        Assert.Contains("op_psp_events_total{source=", metrics, StringComparison.Ordinal);
        Assert.Contains("outcome=", metrics, StringComparison.Ordinal);
        Assert.Contains("op_psp_events_parked", metrics, StringComparison.Ordinal);
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(db.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client, Guid account, string body, string signature)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/psp/v1/accounts/{account:D}/events")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Content.Headers.Add("Mock-Signature", signature);
        return await client.SendAsync(request);
    }

    private static string Sign(string body, DateTimeOffset at)
    {
        var timestamp = at.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(Secret));
        var signature = Convert.ToHexString(
            hmac.ComputeHash(Encoding.UTF8.GetBytes(timestamp + "." + body))).ToLowerInvariant();
        return $"t={timestamp},v1={signature}";
    }
}
