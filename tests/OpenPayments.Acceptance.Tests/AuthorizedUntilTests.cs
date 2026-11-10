using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OpenPayments.Api;
using OpenPayments.Domain;
using OpenPayments.Psp;
using OpenPayments.Transitions;
using Xunit;

namespace OpenPayments.Acceptance.Tests;

// Task 28: authorized_until is written by the same transaction that authorizes, whichever
// path answered the PSP, and it is the adapter's authorization window ahead of now.
[Collection("acceptance")]
public sealed class AuthorizedUntilTests(AcceptanceFixture db)
{
    private const string Secret = "whsec_test";

    [Fact]
    public async Task Api_authorize_sets_authorized_until_from_the_authorization_window()
    {
        await db.ResetAsync();
        using var client = Client();
        var now = DateTimeOffset.UtcNow;

        using var response = await CreateAsync(client, "until-api", new
        {
            amount_minor = 1000,
            currency = "USD",
            method = new { type = "card", @ref = "tok_test" },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var id = (await ReadAsync(response)).GetProperty("id").GetString()!;
        Assert.Equal("AUTHORIZED", await db.StateAsync(Guid.Parse(id)));
        var until = await AuthorizedUntilAsync(id);
        Assert.NotNull(until);
        Assert.InRange(until.Value, now.AddDays(7).AddMinutes(-2), now.AddDays(7).AddMinutes(2));
    }

    [Fact]
    public async Task Callback_authorize_sets_authorized_until()
    {
        await db.ResetAsync();
        using var client = Client();
        db.Psp.Next(new PspResult.RequiresAction(
            "3ds", "https://example.test/3ds", DateTimeOffset.UtcNow.AddMinutes(2)));

        using var response = await CreateAsync(client, "until-callback", new
        {
            amount_minor = 1000,
            currency = "USD",
            method = new { type = "card", @ref = "tok_test" },
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var id = (await ReadAsync(response)).GetProperty("id").GetString()!;
        Assert.Equal("ACTION_REQUIRED", await db.StateAsync(Guid.Parse(id)));

        var attempt = await AttemptAsync(id);
        var body = JsonSerializer.Serialize(new
        {
            event_id = "evt_until_callback",
            type = "authorize.approved",
            reference = attempt,
            psp_reference = "psp_until_callback",
            amount_minor = 1000,
            currency = "USD",
        });
        using var callback = await CallbackAsync(client, body);

        Assert.Equal(HttpStatusCode.OK, callback.StatusCode);
        Assert.Equal("AUTHORIZED", await db.StateAsync(Guid.Parse(id)));
        var until = await AuthorizedUntilAsync(id);
        Assert.NotNull(until);
        Assert.InRange(
            until.Value,
            DateTimeOffset.UtcNow.AddDays(7).AddMinutes(-2),
            DateTimeOffset.UtcNow.AddDays(7).AddMinutes(2));
    }

    [Fact]
    public async Task Reconciler_path_sets_authorized_until()
    {
        await db.ResetAsync();
        using var client = Client();
        db.Psp.Next(new PspResult.RequiresAction(
            "3ds", "https://example.test/3ds", DateTimeOffset.UtcNow.AddMinutes(2)));
        using var response = await CreateAsync(client, "until-store", new
        {
            amount_minor = 1000,
            currency = "USD",
            method = new { type = "card", @ref = "tok_test" },
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var id = (await ReadAsync(response)).GetProperty("id").GetString()!;
        var attempt = await AttemptAsync(id);
        var store = db.Factory.Services.GetRequiredService<PaymentStore>();
        var amount = new Money(1000, CurrencyCode.Usd);

        var applied = await store.ApplyPspAnswerAsync(
            new PaymentId(Guid.Parse(id)),
            new PaymentCommand.PspAnswer(
                new AttemptId(Guid.Parse(attempt)),
                AttemptOperation.Authorize,
                PspAnswerOutcome.Approved,
                amount,
                null,
                null,
                DateTimeOffset.UtcNow),
            null,
            CancellationToken.None);

        Assert.IsType<ApplyResult.Applied>(applied);
        Assert.Equal("AUTHORIZED", await db.StateAsync(Guid.Parse(id)));
        var until = await AuthorizedUntilAsync(id);
        Assert.NotNull(until);
        Assert.InRange(
            until.Value,
            DateTimeOffset.UtcNow.AddDays(7).AddMinutes(-2),
            DateTimeOffset.UtcNow.AddDays(7).AddMinutes(2));
    }

    private HttpClient Client()
    {
        var client = db.Client();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "live_key_a");
        return client;
    }

    private static async Task<HttpResponseMessage> CreateAsync(HttpClient client, string key, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/payments")
        {
            Content = JsonContent.Create(body, options: ApiJson.Options),
        };
        request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> CallbackAsync(HttpClient client, string body)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(Secret));
        var signature = Convert.ToHexString(
            hmac.ComputeHash(Encoding.UTF8.GetBytes(timestamp + "." + body))).ToLowerInvariant();
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/psp/v1/accounts/{db.PspAccountA:D}/events")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Content.Headers.Add("Mock-Signature", $"t={timestamp},v1={signature}");
        return await client.SendAsync(request);
    }

    private async Task<string> AttemptAsync(string paymentId)
    {
        await using var connection = await db.OpenAdminAsync();
        await using var command = new NpgsqlCommand(
            "select attempt_id from payment_attempts where payment_id = @id limit 1", connection);
        command.Parameters.AddWithValue("id", Guid.Parse(paymentId));
        return (await command.ExecuteScalarAsync())!.ToString()!;
    }

    private async Task<DateTimeOffset?> AuthorizedUntilAsync(string paymentId)
    {
        await using var connection = await db.OpenAdminAsync();
        await using var command = new NpgsqlCommand(
            "select authorized_until from payments where id = @id", connection);
        command.Parameters.AddWithValue("id", Guid.Parse(paymentId));
        var value = await command.ExecuteScalarAsync();
        return value is DBNull or null
            ? null
            : new DateTimeOffset((DateTime)value, TimeSpan.Zero);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
}
