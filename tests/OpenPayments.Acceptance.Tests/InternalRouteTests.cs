using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using OpenPayments.Api;
using OpenPayments.Domain;
using OpenPayments.Psp;
using Xunit;

namespace OpenPayments.Acceptance.Tests;

// Task C5's edge: the console's resolution route is internal, needs its own scope, and applies
// through the same store transaction as every other writer.
[Collection("acceptance")]
public sealed class InternalRouteTests(AcceptanceFixture db)
{
    private const string ConsoleKey = "console_resolve_key";

    [Fact]
    public async Task Applied_route_returns_the_new_state()
    {
        await db.ResetAsync();
        var (payment, attempt) = await ReviewPaymentAsync("internal-apply");
        var resolution = await SeedResolutionAsync(payment, attempt, Guid.NewGuid());
        using var client = await ConsoleClientAsync();
        var approver = Guid.NewGuid();

        using var response = await ApplyAsync(client, resolution, approver);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("AUTHORIZED", body.RootElement.GetProperty("state").GetString());
        Assert.Equal("applied", await db.TextAsync(
            $"select state from console.review_resolutions where id = '{resolution}'"));
    }

    [Fact]
    public async Task Route_requires_console_resolve_scope()
    {
        await db.ResetAsync();
        using var client = db.Client();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "live_key_a");

        using var response = await ApplyAsync(client, Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Self_approval_returns_403()
    {
        await db.ResetAsync();
        var (payment, attempt) = await ReviewPaymentAsync("internal-self");
        var proposer = Guid.NewGuid();
        var resolution = await SeedResolutionAsync(payment, attempt, proposer);
        using var client = await ConsoleClientAsync();

        using var response = await ApplyAsync(client, resolution, proposer);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Payment_that_left_review_returns_409_with_the_state()
    {
        await db.ResetAsync();
        var (payment, attempt) = await ReviewPaymentAsync("internal-moved");
        var resolution = await SeedResolutionAsync(payment, attempt, Guid.NewGuid());
        await using (var connection = await db.OpenAdminAsync())
        await using (var command = new NpgsqlCommand(
            $"update payments set state = 'CANCELED' where id = '{payment}'", connection))
        {
            await command.ExecuteNonQueryAsync();
        }

        using var client = await ConsoleClientAsync();
        using var response = await ApplyAsync(client, resolution, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("state_changed", body.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Route_absent_when_internal_disabled()
    {
        await db.ResetAsync();
        using var factory = db.Factory.WithWebHostBuilder(
            builder => builder.UseSetting("Internal:Enabled", "false"));
        using var client = factory.CreateClient();

        using var response = await ApplyAsync(client, Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<(Guid Payment, Guid Attempt)> ReviewPaymentAsync(string key)
    {
        db.Psp.Next(new PspResult.Approved(
            "psp_review", new Money(999, CurrencyCode.Usd)));
        using var client = db.Client();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "live_key_a");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/payments")
        {
            Content = JsonContent.Create(new
            {
                amount_minor = 1000,
                currency = "USD",
                method = new { type = "card", @ref = "tok_test" },
            }, options: ApiJson.Options),
        };
        request.Headers.Add("Idempotency-Key", key);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("REVIEW", body.RootElement.GetProperty("state").GetString());
        var payment = Guid.Parse(body.RootElement.GetProperty("id").GetString()!);
        await using var connection = await db.OpenAdminAsync();
        await using var query = new NpgsqlCommand(
            "select attempt_id from payment_attempts where payment_id = @id limit 1", connection);
        query.Parameters.AddWithValue("id", payment);
        var attempt = (Guid)(await query.ExecuteScalarAsync())!;
        return (payment, attempt);
    }

    private async Task<Guid> SeedResolutionAsync(Guid payment, Guid attempt, Guid proposer)
    {
        var id = Guid.NewGuid();
        await using var connection = await db.OpenAdminAsync();
        await using var command = new NpgsqlCommand(
            """
            insert into console.review_resolutions
              (id, payment_id, attempt_id, kind, outcome, psp_answer, evidence, state, proposed_by)
            values (@id, @payment, @attempt, 'payment', 'authorize', 'approved',
                    'checked the PSP dashboard', 'proposed', @proposer)
            """,
            connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("payment", payment);
        command.Parameters.AddWithValue("attempt", attempt);
        command.Parameters.AddWithValue("proposer", proposer);
        await command.ExecuteNonQueryAsync();
        return id;
    }

    private async Task<HttpClient> ConsoleClientAsync()
    {
        await using var connection = await db.OpenAdminAsync();
        await using var command = new NpgsqlCommand(
            """
            insert into api_keys (key_hash, merchant_id, mode, scopes)
            values (@hash, @merchant, 'live', array['console:resolve'])
            on conflict (key_hash) do nothing
            """,
            connection);
        command.Parameters.AddWithValue("hash", SHA256.HashData(Encoding.UTF8.GetBytes(ConsoleKey)));
        command.Parameters.AddWithValue("merchant", db.MerchantA.Value);
        await command.ExecuteNonQueryAsync();
        var client = db.Client();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ConsoleKey);
        return client;
    }

    private static async Task<HttpResponseMessage> ApplyAsync(HttpClient client, Guid resolution, Guid approver)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/internal/v1/resolutions/{resolution:D}/apply")
        {
            Content = JsonContent.Create(new { approver_staff_id = approver }, options: ApiJson.Options),
        };
        return await client.SendAsync(request);
    }
}
