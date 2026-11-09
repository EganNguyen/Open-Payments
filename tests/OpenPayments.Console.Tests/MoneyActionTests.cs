using System.Net;
using System.Text.RegularExpressions;
using Npgsql;
using OpenPayments.Console.Data;
using OpenPayments.Console.Security;
using Xunit;

namespace OpenPayments.Console.Tests;

// Task C11: money actions, against a fake API that counts calls per key.
[Collection("console-db")]
public sealed partial class MoneyActionTests(ConsoleDb db)
{
    [Fact]
    public async Task Double_submit_sends_the_same_key_and_shows_one_result()
    {
        var (factory, client, id) = await ArrangeAsync("AUTHORIZED");
        await using var _ = factory;
        var form = await OpenFormAsync(client, id, "capture");

        var first = await PostAsync(client, id, "capture", form, ("Reason", "Customer confirmed"), ("Step", "confirm"));
        var second = await PostAsync(client, id, "capture", form, ("Reason", "Customer confirmed"), ("Step", "confirm"));

        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, second.StatusCode);
        Assert.Single(factory.Api.Calls.Select(call => call.Key).Distinct());
        Assert.Equal(2, factory.Api.Calls.Count); // the API replays its stored answer for the second
        Assert.Equal(1, await CountAsync($"select count(distinct idempotency_key) from console.audit_log where payment_id = '{id}'"));
    }

    [Fact]
    public async Task Resubmit_after_network_loss_reuses_key_and_shows_real_result()
    {
        var (factory, client, id) = await ArrangeAsync("AUTHORIZED");
        await using var _ = factory;
        var form = await OpenFormAsync(client, id, "capture");
        factory.Api.ThenTimeout();

        var lost = await PostAsync(client, id, "capture", form, ("Reason", "Phone"), ("Step", "confirm"));
        Assert.Contains("Still checking", System.Net.WebUtility.HtmlDecode(await lost.Content.ReadAsStringAsync()), StringComparison.Ordinal);

        // The payment moved on meanwhile; the resubmit goes straight to the API with the same key.
        await db.ExecAsync("primary", $"update payments set state = 'CAPTURED', captured_minor = amount_minor where id = '{id}'");
        var retry = await PostAsync(client, id, "capture", form, ("Reason", "Phone"), ("Step", "confirm"));

        Assert.Equal(HttpStatusCode.Redirect, retry.StatusCode);
        Assert.Equal("/Payments/" + id + "?fresh=1", retry.Headers.Location!.OriginalString);
        Assert.Single(factory.Api.Calls.Select(call => call.Key).Distinct());
        Assert.Equal(2, factory.Api.Calls.Count);
    }

    [Fact]
    public async Task Confirmation_repeats_exact_amount_currency_and_effect()
    {
        var (factory, client, id) = await ArrangeAsync("CAPTURED", amountMinor: 105000, currency: "JPY");
        await using var _ = factory;
        var form = await OpenFormAsync(client, id, "refund");

        var review = await PostAsync(client, id, "refund", form, ("Amount", "1,050"), ("Reason", "Damaged"), ("Step", "review"));
        var html = System.Net.WebUtility.HtmlDecode(await review.Content.ReadAsStringAsync());

        Assert.Contains("Refund 1,050 JPY to the customer.", html, StringComparison.Ordinal);
        Assert.Contains("name=\"Step\" value=\"confirm\"", html, StringComparison.Ordinal);
        Assert.Empty(factory.Api.Calls);
    }

    [Fact]
    public async Task Empty_reason_is_refused_with_no_api_call()
    {
        var (factory, client, id) = await ArrangeAsync("AUTHORIZED");
        await using var _ = factory;
        var form = await OpenFormAsync(client, id, "void");

        var response = await PostAsync(client, id, "void", form, ("Reason", "  "), ("Step", "confirm"));

        Assert.Contains("Write why you're doing this.", System.Net.WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()), StringComparison.Ordinal);
        Assert.Empty(factory.Api.Calls);
        Assert.Equal(0, await CountAsync("select count(*) from console.audit_log"));
    }

    [Fact]
    public async Task Refund_above_refundable_is_refused_beside_the_field()
    {
        var (factory, client, id) = await ArrangeAsync("CAPTURED", amountMinor: 1050);
        await using var _ = factory;
        var form = await OpenFormAsync(client, id, "refund");

        var response = await PostAsync(client, id, "refund", form, ("Amount", "10.51"), ("Reason", "Too much"), ("Step", "review"));
        var html = System.Net.WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Contains("id=\"amount-error\"", html, StringComparison.Ordinal);
        Assert.Contains("At most 10.50 USD can be refunded.", html, StringComparison.Ordinal);
        Assert.Empty(factory.Api.Calls);
    }

    [Fact]
    public async Task Answer_202_shows_still_checking_never_failed()
    {
        var (factory, client, id) = await ArrangeAsync("AUTHORIZED");
        await using var _ = factory;
        var form = await OpenFormAsync(client, id, "capture");
        factory.Api.Then(HttpStatusCode.Accepted, """{"state":"PSP_PENDING"}""");

        var html = System.Net.WebUtility.HtmlDecode(await (await PostAsync(client, id, "capture", form, ("Reason", "r"), ("Step", "confirm"))).Content.ReadAsStringAsync());

        Assert.Contains("Still checking", html, StringComparison.Ordinal);
        Assert.DoesNotContain("failed", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Timeout_shows_still_checking_never_failed()
    {
        var (factory, client, id) = await ArrangeAsync("AUTHORIZED");
        await using var _ = factory;
        var form = await OpenFormAsync(client, id, "capture");
        factory.Api.ThenTimeout();

        var html = System.Net.WebUtility.HtmlDecode(await (await PostAsync(client, id, "capture", form, ("Reason", "r"), ("Step", "confirm"))).Content.ReadAsStringAsync());

        Assert.Contains("Still checking", html, StringComparison.Ordinal);
        Assert.DoesNotContain("failed", html, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await CountAsync("select count(*) from console.audit_log where phase = 'outcome' and result = 'still_checking'"));
    }

    [Fact]
    public async Task Answer_409_reloads_and_explains_and_does_not_retry()
    {
        var (factory, client, id) = await ArrangeAsync("AUTHORIZED");
        await using var _ = factory;
        var form = await OpenFormAsync(client, id, "void");
        factory.Api.Then(HttpStatusCode.Conflict, """{"code":"operation_live","status":409,"title":"x"}""");

        var html = System.Net.WebUtility.HtmlDecode(await (await PostAsync(client, id, "void", form, ("Reason", "r"), ("Step", "confirm"))).Content.ReadAsStringAsync());

        Assert.Contains("operation_live", html, StringComparison.Ordinal);
        Assert.Contains("Nothing was retried.", html, StringComparison.Ordinal);
        Assert.Single(factory.Api.Calls);
    }

    [Fact]
    public async Task Answer_429_keeps_form_values_and_same_key()
    {
        var (factory, client, id) = await ArrangeAsync("CAPTURED", amountMinor: 5000);
        await using var _ = factory;
        var form = await OpenFormAsync(client, id, "refund");
        factory.Api.Then(HttpStatusCode.TooManyRequests, "{}", TimeSpan.FromSeconds(2));

        var busy = await PostAsync(client, id, "refund", form, ("Amount", "12.34"), ("Reason", "Late delivery"), ("Step", "confirm"));
        var html = System.Net.WebUtility.HtmlDecode(await busy.Content.ReadAsStringAsync());

        Assert.Contains("value=\"12.34\"", html, StringComparison.Ordinal);
        Assert.Contains("Late delivery", html, StringComparison.Ordinal);
        Assert.Contains(form.Token, html, StringComparison.Ordinal);
        Assert.Contains("Wait 2 s", html, StringComparison.Ordinal);

        var retry = await PostAsync(client, id, "refund", form, ("Amount", "12.34"), ("Reason", "Late delivery"), ("Step", "confirm"));
        Assert.Equal(HttpStatusCode.Redirect, retry.StatusCode);
        Assert.Single(factory.Api.Calls.Select(call => call.Key).Distinct());
        Assert.All(factory.Api.Calls, call => Assert.Contains("\"amount_minor\":1234", call.Body, StringComparison.Ordinal));
    }

    [Fact]
    public async Task State_changed_before_submit_explains_with_no_api_call()
    {
        var (factory, client, id) = await ArrangeAsync("AUTHORIZED");
        await using var _ = factory;
        var form = await OpenFormAsync(client, id, "capture");
        await db.ExecAsync("primary", $"update payments set state = 'VOIDED' where id = '{id}'");

        var html = System.Net.WebUtility.HtmlDecode(await (await PostAsync(client, id, "capture", form, ("Reason", "r"), ("Step", "confirm"))).Content.ReadAsStringAsync());

        Assert.Contains("This payment changed to Voided before you confirmed, so nothing was sent.", html, StringComparison.Ordinal);
        Assert.Empty(factory.Api.Calls);
    }

    [Fact]
    public async Task Viewer_post_is_forbidden_with_no_intent_and_no_api_call()
    {
        await db.ResetAsync();
        var id = await db.SeedPaymentAsync("primary", "AUTHORIZED");
        await using var factory = new DbConsoleFactory(db);
        var operatorClient = factory.ClientFor(factory.AddStaff(ConsoleRole.Operator).Subject);
        var form = await OpenFormAsync(operatorClient, id, "capture");
        using var viewer = factory.ClientFor(factory.AddStaff(ConsoleRole.Viewer).Subject);
        var viewerAntiforgery = Antiforgery().Match(await viewer.GetStringAsync("/Payments")).Groups[1].Value;

        var response = await PostAsync(viewer, id, "capture", form with { Antiforgery = viewerAntiforgery }, ("Reason", "r"), ("Step", "confirm"));

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(factory.Api.Calls);
        Assert.Equal(0, await CountAsync("select count(*) from console.audit_log"));
    }

    [Fact]
    public async Task Stale_auth_redirects_with_no_intent_and_no_api_call()
    {
        await db.ResetAsync();
        var id = await db.SeedPaymentAsync("primary", "CAPTURED", amountMinor: 5000);
        await using var factory = new DbConsoleFactory(db);
        var staff = factory.AddStaff(ConsoleRole.Operator);
        using var fresh = factory.ClientFor(staff.Subject);
        var form = await OpenFormAsync(fresh, id, "refund");
        using var stale = factory.ClientFor(staff.Subject, authAgeSeconds: 600);
        var staleAntiforgery = Antiforgery().Match(await stale.GetStringAsync("/Payments")).Groups[1].Value;

        var response = await PostAsync(stale, id, "refund", form with { Antiforgery = staleAntiforgery }, ("Amount", "7.00"), ("Reason", "Kept"), ("Step", "confirm"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.OriginalString;
        Assert.StartsWith("/test-idp/authorize?", location, StringComparison.Ordinal);
        Assert.Contains("prompt=login", location, StringComparison.Ordinal);
        Assert.Contains("max_age=300", location, StringComparison.Ordinal);
        Assert.Empty(factory.Api.Calls);
        Assert.Equal(0, await CountAsync("select count(*) from console.audit_log"));
    }

    [Fact]
    public async Task Stale_auth_time_redirects_to_reauth_and_preserves_form()
    {
        // Task C6's test, placed here because this is the first form that needs recent sign-in.
        await db.ResetAsync();
        var id = await db.SeedPaymentAsync("primary", "CAPTURED", amountMinor: 5000);
        await using var factory = new DbConsoleFactory(db);
        var staff = factory.AddStaff(ConsoleRole.Operator);
        using var client = factory.ClientFor(staff.Subject, authAgeSeconds: 600);
        var form = await OpenFormAsync(client, id, "refund");

        var response = await PostAsync(client, id, "refund", form, ("Amount", "7.00"), ("Reason", "Kept value"), ("Step", "review"));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var returnUrl = Uri.UnescapeDataString(ReturnUrl().Match(response.Headers.Location!.OriginalString).Groups[1].Value);
        Assert.Equal($"/Payments/{id}/Action/refund", returnUrl);

        // Back from the identity provider, the same form shows the values typed before.
        var html = await GetTextAsync(client, returnUrl);
        Assert.Contains("value=\"7.00\"", html, StringComparison.Ordinal);
        Assert.Contains("Kept value", html, StringComparison.Ordinal);
        Assert.Contains(form.Token, html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Key_from_another_payment_or_person_is_refused()
    {
        await db.ResetAsync();
        var first = await db.SeedPaymentAsync("primary", "AUTHORIZED");
        var second = await db.SeedPaymentAsync("primary", "AUTHORIZED");
        await using var factory = new DbConsoleFactory(db);
        using var alice = factory.ClientFor(factory.AddStaff(ConsoleRole.Operator).Subject);
        using var bob = factory.ClientFor(factory.AddStaff(ConsoleRole.Operator).Subject);
        var aliceForm = await OpenFormAsync(alice, first, "capture");

        var otherPayment = await PostAsync(alice, second, "capture", aliceForm, ("Reason", "r"), ("Step", "confirm"));
        var bobAntiforgery = Antiforgery().Match(await bob.GetStringAsync("/Payments")).Groups[1].Value;
        var otherPerson = await PostAsync(bob, first, "capture", aliceForm with { Antiforgery = bobAntiforgery }, ("Reason", "r"), ("Step", "confirm"));
        var otherAction = await PostAsync(alice, first, "void", aliceForm, ("Reason", "r"), ("Step", "confirm"));

        Assert.Equal(HttpStatusCode.BadRequest, otherPayment.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, otherPerson.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, otherAction.StatusCode);
        Assert.Empty(factory.Api.Calls);
    }

    [Fact]
    public async Task Intent_write_failure_means_no_api_call()
    {
        await db.ResetAsync();
        var id = await db.SeedPaymentAsync("primary", "AUTHORIZED");
        await using var factory = new DbConsoleFactory(db) { AuditWriterOverride = new FailingIntentWriter() };
        using var client = factory.ClientFor(factory.AddStaff(ConsoleRole.Operator).Subject);
        var form = await OpenFormAsync(client, id, "capture");

        var html = System.Net.WebUtility.HtmlDecode(await (await PostAsync(client, id, "capture", form, ("Reason", "r"), ("Step", "confirm"))).Content.ReadAsStringAsync());

        Assert.Contains("The action wasn't sent, because the audit record couldn't be written.", html, StringComparison.Ordinal);
        Assert.Empty(factory.Api.Calls);
    }

    [Fact]
    public async Task Every_action_writes_intent_before_call_and_outcome_after()
    {
        await db.ResetAsync();
        await using var factory = new DbConsoleFactory(db);
        using var client = factory.ClientFor(factory.AddStaff(ConsoleRole.Operator).Subject);
        foreach (var (state, action, extra) in new[]
        {
            ("AWAITING_METHOD", "cancel", (string?)null),
            ("AUTHORIZED", "capture", null),
            ("AUTHORIZED", "void", null),
            ("CAPTURED", "refund", "3.00"),
        })
        {
            var id = await db.SeedPaymentAsync("primary", state, amountMinor: 5000);
            var form = await OpenFormAsync(client, id, action);
            var fields = new List<(string, string)> { ("Reason", "Checked"), ("Step", "confirm") };
            if (extra is not null)
            {
                fields.Add(("Amount", extra));
            }

            var before = factory.Api.Calls.Count;
            var response = await PostAsync(client, id, action, form, [.. fields]);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal(before + 1, factory.Api.Calls.Count);

            var phases = await PhasesAsync(id);
            Assert.Equal(["intent", "outcome"], phases);
        }
    }

    private async Task<(DbConsoleFactory Factory, HttpClient Client, Guid Id)> ArrangeAsync(string state, long amountMinor = 5000, string currency = "USD")
    {
        await db.ResetAsync();
        var id = await db.SeedPaymentAsync("primary", state, amountMinor: amountMinor, currency: currency);
        var factory = new DbConsoleFactory(db);
        var client = factory.ClientFor(factory.AddStaff(ConsoleRole.Operator).Subject);
        return (factory, client, id);
    }

    private static async Task<Form> OpenFormAsync(HttpClient client, Guid id, string action)
    {
        var response = await client.GetAsync($"/Payments/{id}/Action/{action}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = System.Net.WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        return new Form(TokenField().Match(html).Groups[1].Value, Antiforgery().Match(html).Groups[1].Value);
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, Guid id, string action, Form form, params (string Name, string Value)[] fields)
    {
        var values = new List<KeyValuePair<string, string>>
        {
            new("Token", form.Token),
            new("__RequestVerificationToken", form.Antiforgery),
        };
        values.AddRange(fields.Select(field => new KeyValuePair<string, string>(field.Name, field.Value)));
        using var body = new FormUrlEncodedContent(values);
        return await client.PostAsync($"/Payments/{id}/Action/{action}", body);
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(db.AdminFor("primary"));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<List<string>> PhasesAsync(Guid payment)
    {
        await using var connection = new NpgsqlConnection(db.AdminFor("primary"));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"select phase from console.audit_log where payment_id = '{payment}' order by id", connection);
        var phases = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            phases.Add(reader.GetString(0));
        }

        return phases;
    }

    private sealed record Form(string Token, string Antiforgery);

    private sealed class FailingIntentWriter : IAuditWriter
    {
        public Task WriteIntentAsync(AuditIntent intent, CancellationToken ct) => throw new NpgsqlException("primary down");

        public Task WriteOutcomeAsync(string idempotencyKey, Guid staffId, string result, CancellationToken ct) => Task.CompletedTask;

        public Task WriteEventAsync(Guid staffId, string action, string? detail, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<AuditIntentRow>> FindOrphanIntentsAsync(TimeSpan olderThan, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AuditIntentRow>>([]);

        public Task<bool> IntentExistsAsync(string idempotencyKey, CancellationToken ct) => Task.FromResult(false);
    }

    [GeneratedRegex("name=\"Token\" value=\"([^\"]+)\"")]
    private static partial Regex TokenField();

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex Antiforgery();

    [GeneratedRegex("returnUrl=([^&]+)")]
    private static partial Regex ReturnUrl();

    private static async Task<string> GetTextAsync(HttpClient client, string url) =>
        System.Net.WebUtility.HtmlDecode(await client.GetStringAsync(url));
}
