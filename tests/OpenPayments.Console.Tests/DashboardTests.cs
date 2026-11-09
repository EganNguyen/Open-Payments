using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using OpenPayments.Console.Data;
using OpenPayments.Console.Security;
using Xunit;

namespace OpenPayments.Console.Tests;

// The home operations dashboard: throughput, reliability, lifecycle and correctness, with
// the work queue at /attention.
[Collection("console-db")]
public sealed partial class DashboardTests(ConsoleDb db)
{
    [Fact]
    public async Task Home_shows_the_four_panels_and_the_attention_strip()
    {
        await db.ResetAsync();
        await using var factory = new DbConsoleFactory(db);
        var now = factory.Clock.GetUtcNow();
        await db.SeedPaymentAsync("replica", "REVIEW", amountMinor: 31050, createdAt: now.AddMinutes(-5));
        var authorized = await db.SeedPaymentAsync("replica", "AUTHORIZED", createdAt: now.AddMinutes(-3));
        await db.ExecAsync("replica", $"update payments set authorized_until = '{now.AddHours(9):O}' where id = '{authorized}'");
        using var client = Client(factory, ConsoleRole.Viewer);

        var html = await GetAsync(client, "/");

        Assert.Contains("<h1>Dashboard</h1>", html, StringComparison.Ordinal);
        Assert.Contains("data-panel=\"throughput\"", html, StringComparison.Ordinal);
        Assert.Contains("data-panel=\"reliability\"", html, StringComparison.Ordinal);
        Assert.Contains("data-panel=\"lifecycle\"", html, StringComparison.Ordinal);
        Assert.Contains("data-panel=\"correctness\"", html, StringComparison.Ordinal);

        var attention = Section(html, "attention");
        Assert.Contains("Needs review", attention, StringComparison.Ordinal);
        Assert.Contains("href=\"/attention#q-review\"", attention, StringComparison.Ordinal);
        Assert.Contains("Authorizations ending soon", attention, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Throughput_panel_shows_the_metrics_values()
    {
        await db.ResetAsync();
        await using var factory = new DbConsoleFactory(db);
        using var client = Client(factory, ConsoleRole.Viewer);

        var html = await GetAsync(client, "/");

        var panel = Section(html, "throughput");
        Assert.Contains("123.45 /s", panel, StringComparison.Ordinal);
        Assert.Contains("150 /s", panel, StringComparison.Ordinal);
        Assert.Contains("2.5 /s", panel, StringComparison.Ordinal);
        Assert.Contains("4 ms", panel, StringComparison.Ordinal);
        Assert.Contains("42 ms", panel, StringComparison.Ordinal);
        Assert.Contains("250 ms", panel, StringComparison.Ordinal);
        Assert.Contains("12", panel, StringComparison.Ordinal);
        Assert.Contains("Most rejections: busy", panel, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Throughput_panel_degrades_when_metrics_are_unavailable()
    {
        await db.ResetAsync();
        await using var factory = new DbConsoleFactory(db);
        factory.Metrics.Snapshot = null;
        using var client = Client(factory, ConsoleRole.Viewer);

        var html = await GetAsync(client, "/");

        Assert.Contains("Throughput metrics are unavailable.", Section(html, "throughput"), StringComparison.Ordinal);
        Assert.Contains("data-panel=\"lifecycle\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lifecycle_shows_recent_payments_by_state_and_hides_older_ones()
    {
        await db.ResetAsync();
        await using var factory = new DbConsoleFactory(db);
        var now = factory.Clock.GetUtcNow();
        await db.SeedPaymentAsync("replica", "REVIEW", createdAt: now.AddMinutes(-5));
        await db.SeedPaymentAsync("replica", "REVIEW", createdAt: now.AddMinutes(-30));
        using var client = Client(factory, ConsoleRole.Viewer);

        var html = await GetAsync(client, "/");

        var lifecycle = Section(html, "lifecycle");
        Assert.Single(LifecycleRows(lifecycle));
        Assert.Contains("data-lifecycle-state=\"REVIEW\"", lifecycle, StringComparison.Ordinal);
        Assert.Contains("Needs review", lifecycle, StringComparison.Ordinal);
        Assert.Contains("15 min", lifecycle, StringComparison.Ordinal);
        // The queue still counts both; only the lifecycle window is bounded.
        Assert.Contains("Needs review", Section(html, "attention"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lifecycle_and_attention_say_when_there_is_no_recent_work()
    {
        await db.ResetAsync();
        await using var factory = new DbConsoleFactory(db);
        using var client = Client(factory, ConsoleRole.Viewer);

        var html = await GetAsync(client, "/");

        Assert.Contains("No payments were created in this window.", Section(html, "lifecycle"), StringComparison.Ordinal);
        Assert.Contains("Nothing needs attention.", Section(html, "attention"), StringComparison.Ordinal);
        Assert.Contains("Pass", Section(html, "correctness"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Correctness_panel_reports_live_invariant_issues()
    {
        await db.ResetAsync();
        await using var factory = new DbConsoleFactory(db);
        var now = factory.Clock.GetUtcNow();
        await db.SeedPaymentAsync("replica", "CAPTURED", amountMinor: 5000, createdAt: now.AddMinutes(-2));
        using var client = Client(factory, ConsoleRole.Viewer);

        var html = await GetAsync(client, "/");

        var correctness = Section(html, "correctness");
        Assert.Contains("data-check=\"capture_ledger\" data-issues=\"1\"", correctness, StringComparison.Ordinal);
        Assert.Contains("data-check=\"capture_amount\" data-issues=\"1\"", correctness, StringComparison.Ordinal);
        Assert.Contains("1 issue", correctness, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reliability_panel_shows_lag_and_stuck_work()
    {
        await db.ResetAsync();
        await using var factory = new DbConsoleFactory(db);
        var now = factory.Clock.GetUtcNow();
        var unknown = await db.SeedPaymentAsync("replica", "UNKNOWN", createdAt: now.AddMinutes(-5));
        await db.ExecAsync("replica", $"update payments set updated_at = '{now.AddMinutes(-5):O}' where id = '{unknown}'");
        using var client = Client(factory, ConsoleRole.Viewer);

        var html = await GetAsync(client, "/");

        var reliability = Section(html, "reliability");
        Assert.Matches("Waiting over 60 s</dt>\\s*<dd>1</dd>", reliability);
        Assert.Contains("Replica lag", reliability, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lifecycle_query_uses_payments_merchant_created()
    {
        await db.ResetAsync();
        await using var connection = new NpgsqlConnection(db.AdminFor("replica"));
        await connection.OpenAsync();
        await using (var off = new NpgsqlCommand("set enable_seqscan = off", connection))
        {
            await off.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand("explain (format json) " + DashboardReader.LifecycleSql, connection);
        command.Parameters.AddWithValue("merchants", new[] { ConsoleDb.Merchant });
        command.Parameters.AddWithValue("from", DateTimeOffset.UtcNow.AddMinutes(-15));
        var plan = JsonDocument.Parse((string)(await command.ExecuteScalarAsync())!).RootElement[0].GetProperty("Plan");

        Assert.Contains(
            Walk(plan),
            node => node.TryGetProperty("Index Name", out var name)
                && name.GetString()!.Contains("merchant_id_created_at", StringComparison.Ordinal));
        Assert.DoesNotContain(Walk(plan), node => node.GetProperty("Node Type").GetString() == "Seq Scan");
    }

    [Fact]
    public async Task Navigation_puts_dashboard_first_and_keeps_the_attention_badge()
    {
        await db.ResetAsync();
        await using var factory = new DbConsoleFactory(db);
        await db.SeedPaymentAsync("replica", "REVIEW");
        using var client = Client(factory, ConsoleRole.Operator);

        var html = await GetAsync(client, "/");

        var sidebar = Between(html, "<nav class=\"sidebar\"", "</nav>");
        Assert.Equal(["/", "/attention", "/Payments", "/Audit", "/Staff"], Hrefs(sidebar));
        Assert.Contains("aria-current=\"page\"", Between(sidebar, "href=\"/\"", "</a>"), StringComparison.Ordinal);
        Assert.Contains("Needs attention, 1 to act on", html, StringComparison.Ordinal);
        var tabs = Between(html, "<nav class=\"tabs\"", "</nav>");
        Assert.Equal(["/", "/attention", "/Payments", "/More"], Hrefs(tabs));
    }

    [Fact]
    public async Task Attention_page_is_still_the_work_queue()
    {
        await db.ResetAsync();
        await using var factory = new DbConsoleFactory(db);
        await db.SeedPaymentAsync("replica", "REVIEW");
        using var client = Client(factory, ConsoleRole.Viewer);

        var attention = await GetAsync(client, "/attention");

        Assert.Contains("<h1>Needs attention</h1>", attention, StringComparison.Ordinal);
        Assert.Contains("data-queue=\"review\"", attention, StringComparison.Ordinal);
        Assert.DoesNotContain("data-panel=\"throughput\"", attention, StringComparison.Ordinal);
    }

    private static HttpClient Client(ConsoleFactory factory, ConsoleRole role) =>
        factory.ClientFor(factory.AddStaff(role).Subject);

    private static async Task<string> GetAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    private static string Section(string html, string panel)
    {
        var match = Regex.Match(html, $"<section[^>]*data-panel=\"{panel}\".*?</section>", RegexOptions.Singleline);
        Assert.True(match.Success, "No section for panel " + panel);
        return match.Value;
    }

    private static List<string> LifecycleRows(string section) =>
        [.. LifecycleRow().Matches(section).Select(match => match.Groups[1].Value)];

    private static List<string> Hrefs(string nav) =>
        [.. Regex.Matches(nav, "<a [^>]*href=\"([^\"]+)\"").Select(match => match.Groups[1].Value)];

    private static string Between(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, "Missing " + start);
        var to = text.IndexOf(end, from, StringComparison.Ordinal);
        return text[from..to];
    }

    private static IEnumerable<JsonElement> Walk(JsonElement plan)
    {
        yield return plan;
        if (plan.TryGetProperty("Plans", out var children))
        {
            foreach (var child in children.EnumerateArray())
            {
                foreach (var node in Walk(child))
                {
                    yield return node;
                }
            }
        }
    }

    [GeneratedRegex("data-lifecycle-state=\"([A-Z_]+)\"")]
    private static partial Regex LifecycleRow();
}
