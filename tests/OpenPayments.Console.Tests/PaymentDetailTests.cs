using System.Net;
using System.Text.RegularExpressions;
using OpenPayments.Console.Presentation;
using OpenPayments.Console.Security;
using Xunit;

namespace OpenPayments.Console.Tests;

// Task C9: the payment page.
[Collection("console-db")]
public sealed partial class PaymentDetailTests(ConsoleDb db)
{
    public static TheoryData<string> States() => [.. StatePresentation.CommittedStates];

    [Theory]
    [MemberData(nameof(States))]
    public async Task Each_state_shows_its_wording_and_only_its_actions(string state)
    {
        await db.ResetAsync();
        var id = await db.SeedPaymentAsync("replica", state);
        await using var factory = new DbConsoleFactory(db);
        using var client = factory.ClientFor(factory.AddStaff(ConsoleRole.Operator).Subject);

        var html = await client.GetStringAsync("/Payments/" + id);

        var view = StatePresentation.For(state);
        Assert.Contains(view.Wording, System.Net.WebUtility.HtmlDecode(html), StringComparison.Ordinal);
        var expected = StatePresentation.Shown(view, canAct: true, resolutionEnabled: false)
            .Select(OpenPayments.Console.Pages.Payments.DetailModel.ActionName);
        Assert.Equal(expected, ActionAttribute().Matches(html).Select(match => match.Groups[1].Value));
    }

    [Fact]
    public async Task Viewer_sees_no_actions()
    {
        await db.ResetAsync();
        var id = await db.SeedPaymentAsync("replica", "CAPTURED");
        await using var factory = new DbConsoleFactory(db);
        using var client = factory.ClientFor(factory.AddStaff(ConsoleRole.Viewer).Subject);

        var html = await client.GetStringAsync("/Payments/" + id);

        Assert.Empty(ActionAttribute().Matches(html));
    }

    [Fact]
    public async Task Unresolved_refund_says_in_progress_never_refunded()
    {
        await db.ResetAsync();
        var id = await db.SeedPaymentAsync("replica", "CAPTURED", amountMinor: 5000);
        await db.SeedRefundAsync("replica", id, 1000, "UNKNOWN", DateTimeOffset.UtcNow.AddMinutes(-3));
        await using var factory = new DbConsoleFactory(db);
        using var client = factory.ClientFor(factory.AddStaff(ConsoleRole.Viewer).Subject);

        var html = await client.GetStringAsync("/Payments/" + id);

        Assert.Contains("In progress, amount reserved", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-label=\"Status\">Refunded<", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refundable_excludes_pending_refunds()
    {
        await db.ResetAsync();
        var id = await db.SeedPaymentAsync("replica", "PARTIALLY_REFUNDED", amountMinor: 5000);
        await db.SeedRefundAsync("replica", id, 1000, "SUCCEEDED", DateTimeOffset.UtcNow.AddMinutes(-5));
        await db.SeedRefundAsync("replica", id, 1500, "PENDING", DateTimeOffset.UtcNow.AddMinutes(-1));
        await using var factory = new DbConsoleFactory(db);
        using var client = factory.ClientFor(factory.AddStaff(ConsoleRole.Viewer).Subject);

        var html = await client.GetStringAsync("/Payments/" + id);

        Assert.Contains("data-refundable=\"2500\"", html, StringComparison.Ordinal);
        Assert.Contains("25.00 USD", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Timeline_is_in_time_order_across_attempts_refunds_ledger_and_staff()
    {
        await db.ResetAsync();
        var start = DateTimeOffset.UtcNow.AddHours(-1);
        var id = await db.SeedPaymentAsync("replica", "PARTIALLY_REFUNDED", amountMinor: 5000, createdAt: start);
        await db.SeedAttemptAsync("replica", id, "sale", "SUCCEEDED", start.AddSeconds(1), pspReference: "psp_1");
        var txn = Guid.CreateVersion7();
        await db.ExecAsync("replica", $"""
            insert into ledger_transactions (id, merchant_id, payment_id, kind, source_event_id, effective_at, created_at)
            values ('{txn}', '{ConsoleDb.Merchant}', '{id}', 'capture', '{txn}', '{start.AddSeconds(2):O}', '{start.AddSeconds(2):O}');
            insert into console.staff (id, oidc_subject, email, role) values ('{Guid.Empty}', 's', 'ops@example.test', 'operator');
            insert into console.audit_log (staff_id, phase, action, payment_id, result, reason, created_at)
            values ('{Guid.Empty}', 'outcome', 'refund', '{id}', 'done', 'Customer asked', '{start.AddSeconds(4):O}');
            """);
        await db.SeedRefundAsync("replica", id, 1000, "SUCCEEDED", start.AddSeconds(3));
        await using var factory = new DbConsoleFactory(db);
        using var client = factory.ClientFor(factory.AddStaff(ConsoleRole.Viewer).Subject);

        var html = await client.GetStringAsync("/Payments/" + id);

        var kinds = TimelineKind().Matches(html).Select(match => match.Groups[1].Value).ToList();
        Assert.Equal(["Attempt", "Ledger", "Refund", "Staff"], kinds);
    }

    [Fact]
    public async Task Fresh_reads_primary_even_when_replica_is_stale()
    {
        await db.ResetAsync();
        var id = Guid.CreateVersion7();
        await db.SeedPaymentAsync("replica", "AUTHORIZED", id: id);
        await db.SeedPaymentAsync("primary", "CAPTURED", id: id);
        await using var factory = new DbConsoleFactory(db);
        using var client = factory.ClientFor(factory.AddStaff(ConsoleRole.Viewer).Subject);

        Assert.Contains("data-state=\"AUTHORIZED\"", await client.GetStringAsync("/Payments/" + id), StringComparison.Ordinal);
        Assert.Contains("data-state=\"CAPTURED\"", await client.GetStringAsync("/Payments/" + id + "?fresh=1"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Html_in_failure_code_and_reason_renders_as_text()
    {
        await db.ResetAsync();
        var id = await db.SeedPaymentAsync("replica", "AWAITING_METHOD");
        await db.SeedAttemptAsync("replica", id, "authorize", "FAILED", DateTimeOffset.UtcNow.AddMinutes(-2),
            failureCode: "<script>alert(1)</script>");
        await db.ExecAsync("replica", $"""
            insert into console.audit_log (staff_id, phase, action, payment_id, result, reason)
            values ('{Guid.NewGuid()}', 'outcome', 'cancel', '{id}', 'refused', '<img src=x onerror=alert(2)>')
            """);
        await using var factory = new DbConsoleFactory(db);
        using var client = factory.ClientFor(factory.AddStaff(ConsoleRole.Viewer).Subject);

        var html = await client.GetStringAsync("/Payments/" + id);

        Assert.DoesNotContain("<script>alert(1)</script>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<img src=x", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_payment_id_shows_not_found()
    {
        await db.ResetAsync();
        await using var factory = new DbConsoleFactory(db);
        using var client = factory.ClientFor(factory.AddStaff(ConsoleRole.Viewer).Subject);

        var response = await client.GetAsync("/Payments/" + Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Payment not found", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [GeneratedRegex("data-action=\"([a-z]+)\"")]
    private static partial Regex ActionAttribute();

    [GeneratedRegex("<li data-kind=\"([A-Za-z]+)\"")]
    private static partial Regex TimelineKind();
}
