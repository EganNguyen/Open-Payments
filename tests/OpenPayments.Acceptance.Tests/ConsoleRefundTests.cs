extern alias ConsoleApp;

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using OpenPayments.Api;
using OpenPayments.Checks;
using Xunit;
using ConsoleProgram = ConsoleApp::Program;

namespace OpenPayments.Acceptance.Tests;

// Task C11 step 4: a refund from the console through the real API host and database.
[Collection("acceptance")]
public sealed partial class ConsoleRefundTests(AcceptanceFixture db)
{
    [Fact]
    public async Task Console_refund_through_real_api_balances_ledger_and_passes_ck1()
    {
        await db.ResetAsync();
        var paymentId = await CapturedPaymentAsync(10000);
        var staffSubject = "operator-" + Guid.NewGuid().ToString("N");
        await SeedConsoleAsync(staffSubject);
        await using var console = new RealApiConsole(db, ConsoleConnection());
        using var client = console.ClientFor(staffSubject);

        var form = await client.GetStringAsync($"/Payments/{paymentId}/Action/refund");
        var token = TokenField().Match(form).Groups[1].Value;
        var antiforgery = Antiforgery().Match(form).Groups[1].Value;
        using var body = new FormUrlEncodedContent(
        [
            new("Token", token),
            new("__RequestVerificationToken", antiforgery),
            new("Amount", "30.00"),
            new("Reason", "Customer returned one item"),
            new("Step", "confirm"),
        ]);
        var response = await client.PostAsync($"/Payments/{paymentId}/Action/refund", body);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/Payments/{paymentId}?fresh=1", response.Headers.Location!.OriginalString);
        Assert.Equal("PARTIALLY_REFUNDED", await db.StateAsync(paymentId));
        Assert.Equal(3000, await db.CountAsync($"select refunded_minor from payments where id = '{paymentId}'"));
        Assert.Equal(1, await db.CountAsync($"select count(*) from refunds where payment_id = '{paymentId}' and state = 'SUCCEEDED' and amount_minor = 3000"));
        Assert.Equal(2, await db.CountAsync($"select count(*) from console.audit_log where payment_id = '{paymentId}' and action = 'refund'"));

        // The money checks judge the result independently of the code that produced it.
        var results = await CheckRunner.RunAsync(new CheckOptions { Db = db.AdminConnectionString, Stage = "phase1" });
        var ck1 = results.Single(result => result.Check == "CK1");
        var ck3 = results.Single(result => result.Check == "CK3");
        Assert.True(ck1.Applicable && ck1.Passed, $"CK1: {ck1.Reason} {string.Join(",", ck1.SampleIds)}");
        Assert.True(ck3.Applicable && ck3.Passed, $"CK3: {ck3.Reason} {string.Join(",", ck3.SampleIds)}");
    }

    private async Task<Guid> CapturedPaymentAsync(long amount)
    {
        using var client = db.Client();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "live_key_a");
        using var create = await PostAsync(client, "/v1/payments", "console-pay", new
        {
            amount_minor = amount,
            currency = "USD",
            method = new { type = "card", @ref = "tok_test" },
        });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        var id = JsonDocument.Parse(await create.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
        using var capture = await PostAsync(client, $"/v1/payments/{id}/capture", "console-pay-capture", new { amount_minor = amount });
        Assert.Equal(HttpStatusCode.OK, capture.StatusCode);
        return Guid.Parse(id);
    }

    private async Task SeedConsoleAsync(string subject)
    {
        await using var connection = await db.OpenAdminAsync();
        await using var command = new NpgsqlCommand(
            $"""
            do $$ begin
              if not exists (select from pg_roles where rolname = 'console_acc_read') then
                create role console_acc_read login password 'op_local_only' in role op_console_read;
              end if;
              if not exists (select from pg_roles where rolname = 'console_acc_write') then
                create role console_acc_write login password 'op_local_only' in role op_console;
              end if;
            end $$;
            insert into console.staff (id, oidc_subject, email, role)
            values ('{Guid.CreateVersion7()}', '{subject}', 'operator@example.test', 'operator');
            """,
            connection);
        await command.ExecuteNonQueryAsync();
    }

    private (string Read, string Write) ConsoleConnection()
    {
        var read = new NpgsqlConnectionStringBuilder(db.AdminConnectionString) { Username = "console_acc_read", Password = "op_local_only" };
        var write = new NpgsqlConnectionStringBuilder(db.AdminConnectionString) { Username = "console_acc_write", Password = "op_local_only" };
        return (read.ConnectionString, write.ConnectionString);
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

    [GeneratedRegex("name=\"Token\" value=\"([^\"]+)\"")]
    private static partial Regex TokenField();

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex Antiforgery();

    /// <summary>The console, signed in by header, calling the fixture's real API host.</summary>
    private sealed class RealApiConsole(AcceptanceFixture db, (string Read, string Write) connections) : WebApplicationFactory<ConsoleProgram>
    {
        public HttpClient ClientFor(string subject)
        {
            var client = CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                BaseAddress = new Uri("https://localhost"),
            });
            client.DefaultRequestHeaders.Add("X-Test-Sub", subject);
            return client;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Console:Db:Primary", connections.Write);
            builder.UseSetting("Console:Db:PrimaryRead", connections.Read);
            builder.UseSetting("Console:Db:Replica", connections.Read);
            builder.UseSetting("Console:Merchants:0", db.MerchantA.Value.ToString("D"));
            builder.UseSetting("Console:Api:BaseUrl", "http://localhost/");
            builder.UseSetting("Console:Api:Keys:" + db.MerchantA.Value.ToString("D"), "live_key_a");
            builder.UseSetting("Console:Oidc:Authority", "https://idp.test/");
            builder.UseSetting("Console:Oidc:ClientId", "acceptance");
            builder.ConfigureTestServices(services =>
            {
                services.AddHttpClient<ConsoleApp::OpenPayments.Console.Actions.PaymentApiClient>()
                    .ConfigurePrimaryHttpMessageHandler(() => db.Factory.Server.CreateHandler());
                services
                    .AddAuthentication(options =>
                    {
                        options.DefaultScheme = HeaderAuth.Name;
                        options.DefaultChallengeScheme = HeaderAuth.Name;
                        options.DefaultAuthenticateScheme = HeaderAuth.Name;
                    })
                    .AddScheme<AuthenticationSchemeOptions, HeaderAuth>(HeaderAuth.Name, _ => { });
            });
        }
    }

    private sealed class HeaderAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string Name = "Header";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var subject = Request.Headers["X-Test-Sub"].ToString();
            if (subject.Length == 0)
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(
            [
                new Claim("sub", subject),
                new Claim("amr", "mfa"),
                new Claim("auth_time", DateTimeOffset.UtcNow.AddSeconds(-30).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
            ], Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Name)));
        }
    }
}
