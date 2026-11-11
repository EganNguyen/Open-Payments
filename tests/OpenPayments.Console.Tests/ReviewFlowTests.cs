using System.Net;
using System.Text.RegularExpressions;
using OpenPayments.Console.Security;
using Xunit;

namespace OpenPayments.Console.Tests;

// C12: proposing, approving and rejecting a REVIEW case. The console never applies money
// itself; the internal route does, and this page only reports its answer.
[Collection("console-db")]
public sealed partial class ReviewFlowTests(ConsoleDb db)
{
    [Fact]
    public async Task Propose_requires_psp_answer_and_evidence()
    {
        await db.ResetAsync();
        await using var factory = new DbConsoleFactory(db);
        var payment = await db.SeedPaymentAsync("replica", "REVIEW");
        var staff = factory.AddStaff(ConsoleRole.Operator);
        using var client = factory.ClientFor(staff.Subject);
        var form = await FormAsync(client, $"/Review/Propose?id={payment}&kind=payment");

        using var response = await PostAsync(client, $"/Review/Propose?id={payment}&kind=payment", form,
            ("PaymentId", payment.ToString()), ("Kind", "payment"), ("Outcome", "authorize"),
            ("PspAnswer", ""), ("Evidence", ""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("A PSP answer and evidence are required.", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(0, await CountAsync("select count(*) from console.review_resolutions"));
    }

    [Fact]
    public async Task Propose_requires_recent_auth()
    {
        await db.ResetAsync();
        await using var factory = new DbConsoleFactory(db);
        var payment = await db.SeedPaymentAsync("replica", "REVIEW");
        var staff = factory.AddStaff(ConsoleRole.Operator);
        using var stale = factory.ClientFor(staff.Subject, authAgeSeconds: 600);
        var form = await FormAsync(stale, $"/Review/Propose?id={payment}&kind=payment");

        using var response = await PostAsync(stale, $"/Review/Propose?id={payment}&kind=payment", form,
            ("PaymentId", payment.ToString()), ("Kind", "payment"), ("Outcome", "authorize"),
            ("PspAnswer", "Approved"), ("Evidence", "PSP record"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("prompt=login", response.Headers.Location!.OriginalString, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Outcome_choices_match_the_case()
    {
        await db.ResetAsync();
        await using var factory = new DbConsoleFactory(db);
        var payment = await db.SeedPaymentAsync("replica", "REVIEW");
        var staff = factory.AddStaff(ConsoleRole.Operator);
        using var client = factory.ClientFor(staff.Subject);

        var paymentHtml = await client.GetStringAsync($"/Review/Propose?id={payment}&kind=payment");
        var refundHtml = await client.GetStringAsync($"/Review/Propose?id={payment}&kind=refund");

        Assert.Contains("value=\"authorize\"", paymentHtml, StringComparison.Ordinal);
        Assert.Contains("value=\"confirm\"", refundHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("value=\"authorize\"", refundHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Viewer_cannot_propose_and_operator_cannot_approve()
    {
        await db.ResetAsync();
        await using var factory = new DbConsoleFactory(db);
        var payment = await db.SeedPaymentAsync("replica", "REVIEW");
        var proposal = await ProposeAsync(payment, Guid.NewGuid());
        using var viewer = factory.ClientFor(factory.AddStaff(ConsoleRole.Viewer).Subject);
        using var op = factory.ClientFor(factory.AddStaff(ConsoleRole.Operator).Subject);

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync($"/Review/Propose?id={payment}&kind=payment")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.GetAsync($"/Review/Decide?id={proposal}")).StatusCode);
    }

    [Fact]
    public async Task Approver_cannot_approve_own_proposal_and_sees_why()
    {
        await db.ResetAsync();
        await using var factory = new DbConsoleFactory(db);
        var payment = await db.SeedPaymentAsync("replica", "REVIEW");
        var approver = factory.AddStaff(ConsoleRole.Approver);
        var proposal = await ProposeAsync(payment, approver.Id);
        using var client = factory.ClientFor(approver.Subject);

        var html = await client.GetStringAsync($"/Review/Decide?id={proposal}");

        Assert.Contains("You proposed this; a different approver must decide it.", html, StringComparison.Ordinal);
        Assert.Contains("disabled", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Second_proposal_on_an_open_case_is_refused_and_links_to_it()
    {
        await db.ResetAsync();
        await using var factory = new DbConsoleFactory(db);
        var payment = await db.SeedPaymentAsync("replica", "REVIEW");
        var open = await ProposeAsync(payment, Guid.NewGuid());
        using var client = factory.ClientFor(factory.AddStaff(ConsoleRole.Operator).Subject);

        using var response = await client.GetAsync($"/Review/Propose?id={payment}&kind=payment");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains($"id={open}", response.Headers.Location!.OriginalString, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Approve_calls_route_once_and_shows_new_state()
    {
        await db.ResetAsync();
        await using var factory = new DbConsoleFactory(db);
        var payment = await db.SeedPaymentAsync("replica", "REVIEW");
        var proposal = await ProposeAsync(payment, Guid.NewGuid());
        var approver = factory.AddStaff(ConsoleRole.Approver);
        using var client = factory.ClientFor(approver.Subject);
        var form = await FormAsync(client, $"/Review/Decide?id={proposal}");

        using var response = await PostAsync(client, $"/Review/Decide?id={proposal}", form,
            ("Id", proposal.ToString()), ("Decision", "approve"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Applied. The payment is now CAPTURED.", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Single(factory.Api.Calls);
        Assert.Contains($"/internal/v1/resolutions/{proposal:D}/apply", factory.Api.Calls.Single().Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Payment_that_left_review_shows_what_changed()
    {
        await db.ResetAsync();
        await using var factory = new DbConsoleFactory(db);
        var payment = await db.SeedPaymentAsync("replica", "REVIEW");
        var proposal = await ProposeAsync(payment, Guid.NewGuid());
        factory.Api.Then(HttpStatusCode.Conflict, """{"error":"state_changed"}""");
        using var client = factory.ClientFor(factory.AddStaff(ConsoleRole.Approver).Subject);
        var form = await FormAsync(client, $"/Review/Decide?id={proposal}");

        using var response = await PostAsync(client, $"/Review/Decide?id={proposal}", form,
            ("Id", proposal.ToString()), ("Decision", "approve"));

        Assert.Contains(
            "The payment changed while this was open: state_changed.",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Html_in_evidence_renders_as_text()
    {
        await db.ResetAsync();
        await using var factory = new DbConsoleFactory(db);
        var payment = await db.SeedPaymentAsync("replica", "REVIEW");
        var proposal = await ProposeAsync(payment, Guid.NewGuid(), "<script>alert(1)</script>");
        using var client = factory.ClientFor(factory.AddStaff(ConsoleRole.Approver).Subject);

        var html = await client.GetStringAsync($"/Review/Decide?id={proposal}");

        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>alert(1)</script>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Late_sale_reversal_says_console_did_not_move_money()
    {
        await db.ResetAsync();
        await using var factory = new DbConsoleFactory(db);
        var payment = await db.SeedPaymentAsync("replica", "REVIEW");
        await db.SeedAttemptAsync("replica", payment, "sale", "REVIEW", factory.Clock.GetUtcNow().AddHours(-3));
        using var client = factory.ClientFor(factory.AddStaff(ConsoleRole.Operator).Subject);

        var html = await client.GetStringAsync($"/Review/Propose?id={payment}&kind=late_reversal");

        Assert.Contains("Reverse this at the PSP first", html, StringComparison.Ordinal);
    }

    private static async Task<string> FormAsync(HttpClient client, string url)
    {
        var html = await client.GetStringAsync(url);
        return Antiforgery().Match(html).Groups[1].Value;
    }

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client, string url, string token, params (string Name, string Value)[] fields)
    {
        var pairs = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", token),
        };
        pairs.AddRange(fields.Select(field => new KeyValuePair<string, string>(field.Name, field.Value)));
        using var content = new FormUrlEncodedContent(pairs);
        return await client.PostAsync(url, content);
    }

    private async Task<Guid> ProposeAsync(Guid payment, Guid proposer, string evidence = "PSP record")
    {
        var id = Guid.NewGuid();
        await db.ExecAsync("replica", $"""
            insert into console.review_resolutions
              (id, payment_id, kind, outcome, psp_answer, evidence, state, proposed_by)
            values ('{id}', '{payment}', 'payment', 'authorize', 'Approved', '{evidence.Replace("'", "''", StringComparison.Ordinal)}', 'proposed', '{proposer}')
            """);
        return id;
    }

    private Task<long> CountAsync(string sql) => ScalarAsync(sql);

    private async Task<long> ScalarAsync(string sql)
    {
        await using var connection = new Npgsql.NpgsqlConnection(db.AdminFor("replica"));
        await connection.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex Antiforgery();
}
