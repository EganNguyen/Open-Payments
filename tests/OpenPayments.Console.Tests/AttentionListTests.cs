using System.Net;
using System.Text.RegularExpressions;
using OpenPayments.Console.Security;
using Xunit;

namespace OpenPayments.Console.Tests;

// N3/N4: the full queue list, the case panel's back link, and the many-unresolved line.
[Collection("console-db")]
public sealed partial class AttentionListTests(ConsoleDb db)
{
    [Fact]
    public async Task List_pages_by_age_fifty_at_a_time()
    {
        await db.ResetAsync();
        await using var factory = new DbConsoleFactory(db);
        await db.ExecAsync("replica", $"""
            insert into payments (id, merchant_id, state, amount_minor, currency, captured_minor, expires_at, created_at, updated_at)
            select gen_random_uuid(), '{ConsoleDb.Merchant}', 'REVIEW', 100, 'USD', 0,
                   now() + interval '1 day', now(), now() - (g || ' minutes')::interval
            from generate_series(1, 51) g
            """);
        using var client = factory.ClientFor(factory.AddStaff(ConsoleRole.Viewer).Subject);

        var first = await client.GetStringAsync("/attention/review");
        var second = await client.GetStringAsync("/attention/review?page=2");

        Assert.Equal(50, Regex.Matches(first, "data-row=").Count);
        Assert.Single(Regex.Matches(second, "data-row="));
        Assert.Contains("Page 1 of 2", first, StringComparison.Ordinal);
        Assert.Contains("Page 2 of 2", second, StringComparison.Ordinal);
        Assert.Contains("Back to what needs attention", second, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_queue_is_404()
    {
        await db.ResetAsync();
        await using var factory = new DbConsoleFactory(db);
        using var client = factory.ClientFor(factory.AddStaff(ConsoleRole.Viewer).Subject);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/attention/nope")).StatusCode);
    }

    [Fact]
    public async Task Case_panel_shows_the_queue_wording_and_ignores_an_unknown_from()
    {
        await db.ResetAsync();
        await using var factory = new DbConsoleFactory(db);
        var payment = await db.SeedPaymentAsync("replica", "REVIEW");
        using var client = factory.ClientFor(factory.AddStaff(ConsoleRole.Viewer).Subject);

        var known = await client.GetStringAsync($"/Payments/{payment}?from=review");
        var unknown = await client.GetStringAsync($"/Payments/{payment}?from=nope");

        Assert.Contains("data-case-panel", known, StringComparison.Ordinal);
        Assert.Contains("compare our record with the PSP", known, StringComparison.Ordinal);
        Assert.DoesNotContain("data-case-panel", unknown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Many_unresolved_points_at_the_configured_psp_status_page()
    {
        await db.ResetAsync();
        await using var factory = new DbConsoleFactory(db, settings =>
            settings["Console:Attention:PspStatusUrl"] = "https://status.example.test");
        await db.ExecAsync("replica", $"""
            insert into payments (id, merchant_id, state, amount_minor, currency, captured_minor, expires_at, created_at, updated_at)
            select gen_random_uuid(), '{ConsoleDb.Merchant}', 'UNKNOWN', 100, 'USD', 0,
                   now() + interval '1 day', now() - interval '2 hours', now() - interval '2 hours'
            from generate_series(1, 50) g
            """);
        using var client = factory.ClientFor(factory.AddStaff(ConsoleRole.Viewer).Subject);

        var html = await client.GetStringAsync("/attention");

        Assert.Contains("data-many-unresolved", html, StringComparison.Ordinal);
        Assert.Contains("https://status.example.test", html, StringComparison.Ordinal);
    }
}
