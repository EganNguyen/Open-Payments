using System.Net;
using Npgsql;
using OpenPayments.Console.Security;
using Xunit;

namespace OpenPayments.Console.Tests;

// Task 18 step 3: what Stage 1 leaves out until Stage 2 adds C12. The home page itself
// arrived early, as N1 and N2: see AttentionTests.
[Collection("console-db")]
public sealed class Stage1LimitTests(ConsoleDb db)
{
    [Fact]
    public async Task Review_payment_shows_wording_and_offers_a_resolution()
    {
        await db.ResetAsync();
        var id = await db.SeedPaymentAsync("replica", "REVIEW");
        await using var factory = new DbConsoleFactory(db);
        using var client = factory.ClientFor(factory.AddStaff(ConsoleRole.Approver).Subject);

        var html = await client.GetStringAsync("/Payments/" + id);

        Assert.Contains("Needs review", html, StringComparison.Ordinal);
        Assert.Contains("The PSP's answer disagrees with our record. A person must compare them.", System.Net.WebUtility.HtmlDecode(html), StringComparison.Ordinal);
        Assert.Contains("data-action=\"propose\"", html, StringComparison.Ordinal);
        Assert.Contains("Propose resolution", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Orphan_intent_is_marked_in_the_audit_log()
    {
        await db.ResetAsync();
        var staff = Guid.NewGuid();
        await db.ExecAsync("primary", $"""
            insert into console.audit_log (staff_id, phase, action, payment_id, idempotency_key, reason, created_at)
            values ('{staff}', 'intent', 'refund', '{Guid.NewGuid()}', 'lost-key', 'r', now() - interval '11 minutes'),
                   ('{staff}', 'intent', 'capture', '{Guid.NewGuid()}', 'done-key', 'r', now() - interval '11 minutes'),
                   ('{staff}', 'outcome', 'capture', null, 'done-key', 'r', now() - interval '10 minutes');
            """);
        await using var factory = new DbConsoleFactory(db);
        using var client = factory.ClientFor(factory.AddStaff(ConsoleRole.Viewer).Subject);

        var html = await client.GetStringAsync("/Audit");

        Assert.Equal(1, html.Split("data-orphan").Length - 1);
        Assert.Contains("No outcome recorded: check the payment", html, StringComparison.Ordinal);
    }
}
