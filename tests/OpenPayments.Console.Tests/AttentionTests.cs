using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using OpenPayments.Console.Data;
using OpenPayments.Console.Security;
using Xunit;

namespace OpenPayments.Console.Tests;

// N1 and N2 of the Needs attention plan (docs/superpowers/plans/2026-11-09-needs-attention-ux.md):
// queue reads, the page, role-aware notes, the badge and navigation. N3 and N4 come with C12.
[Collection("console-db")]
public sealed partial class AttentionTests(ConsoleDb db)
{
    [Fact]
    public async Task Home_serves_needs_attention()
    {
        await db.ResetAsync();
        await using var factory = new DbConsoleFactory(db);
        await db.SeedPaymentAsync("replica", "REVIEW");
        using var client = Client(factory, ConsoleRole.Viewer);

        var response = await client.GetAsync("/attention");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await TextAsync(response);
        Assert.Contains("<h1>Needs attention</h1>", html, StringComparison.Ordinal);
        Assert.Contains("data-queue=\"review\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task All_empty_shows_only_one_calm_panel()
    {
        await db.ResetAsync();
        await using var factory = new DbConsoleFactory(db);
        using var client = Client(factory, ConsoleRole.Viewer);

        var html = await GetAsync(client, "/attention");

        // The owner's decision: the empty page says one thing and nothing else. The check
        // time stays, because it says how fresh that answer is.
        Assert.Contains("<h1>Nothing needs attention.</h1>", html, StringComparison.Ordinal);
        Assert.Contains("Checked <time", html, StringComparison.Ordinal);
        Assert.Contains("12:00:00 UTC", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<h1>Needs attention</h1>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Refresh", html, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"checked\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-queue=", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Also clear", html, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"summary\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Review_lists_payments_attempts_and_refunds_oldest_first()
    {
        await db.ResetAsync();
        await using var factory = new DbConsoleFactory(db);
        var now = factory.Clock.GetUtcNow();
        var payment = await db.SeedPaymentAsync("replica", "REVIEW", amountMinor: 31050);
        await SetUpdatedAsync(payment, now.AddHours(-1));
        var captured = await db.SeedPaymentAsync("replica", "CAPTURED", amountMinor: 15000);
        await db.SeedRefundAsync("replica", captured, 5000, "REVIEW", now.AddHours(-3));
        var withAttempt = await db.SeedPaymentAsync("replica", "CAPTURED", amountMinor: 6400);
        await db.SeedAttemptAsync("replica", withAttempt, "sale", "REVIEW", now.AddHours(-2));
        using var client = Client(factory, ConsoleRole.Viewer);

        var html = await GetAsync(client, "/attention");

        var section = Section(html, "review");
        Assert.Contains("Needs review, 3", section, StringComparison.Ordinal);
        Assert.Equal([captured, withAttempt, payment], RowIds(section));
        Assert.Contains("A refund of 50.00 USD", section, StringComparison.Ordinal);
        Assert.Contains("A sale attempt", section, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Empty_queues_collapse_into_the_also_clear_line()
    {
        await db.ResetAsync();
        await using var factory = new DbConsoleFactory(db);
        await db.SeedPaymentAsync("replica", "REVIEW");
        using var client = Client(factory, ConsoleRole.Viewer);

        var html = await GetAsync(client, "/attention");

        Assert.Single(QueueKeys(html));
        Assert.Contains(
            "Also clear: Waiting for approval, Actions with no recorded result, Unresolved too long, Authorizations ending soon.",
            html,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Queues_appear_in_the_fixed_order()
    {
        await db.ResetAsync();
        await using var factory = new DbConsoleFactory(db);
        var now = factory.Clock.GetUtcNow();
        var expiring = await db.SeedPaymentAsync("replica", "AUTHORIZED");
        await SetAuthorizedUntilAsync(expiring, now.AddHours(9));
        var unknown = await db.SeedPaymentAsync("replica", "UNKNOWN");
        await SetUpdatedAsync(unknown, now.AddHours(-3));
        var review = await db.SeedPaymentAsync("replica", "REVIEW");
        await ProposeAsync(review, Guid.NewGuid());
        await OrphanAsync(review);
        using var client = Client(factory, ConsoleRole.Viewer);

        var html = await GetAsync(client, "/attention");

        Assert.Equal(["review", "approval", "orphan", "unresolved", "expiring"], QueueKeys(html));
    }

    [Fact]
    public async Task Unresolved_lists_only_rows_past_the_age_limit()
    {
        await db.ResetAsync();
        await using var factory = new DbConsoleFactory(db);
        var now = factory.Clock.GetUtcNow();
        var old = await db.SeedPaymentAsync("replica", "UNKNOWN");
        await SetUpdatedAsync(old, now.AddHours(-3).AddMinutes(-12));
        var recent = await db.SeedPaymentAsync("replica", "UNKNOWN");
        await SetUpdatedAsync(recent, now.AddMinutes(-5));
        var capture = await db.SeedPaymentAsync("replica", "AUTHORIZED");
        await db.SeedAttemptAsync("replica", capture, "capture", "PENDING", now.AddMinutes(-40));
        var refunded = await db.SeedPaymentAsync("replica", "CAPTURED", amountMinor: 20000);
        await db.SeedRefundAsync("replica", refunded, 7500, "UNKNOWN", now.AddHours(-5));
        // An authorize attempt is shown through its payment, not on its own.
        var pending = await db.SeedPaymentAsync("replica", "PSP_PENDING");
        await SetUpdatedAsync(pending, now.AddMinutes(-1));
        await db.SeedAttemptAsync("replica", pending, "authorize", "PENDING", now.AddHours(-1));
        using var client = Client(factory, ConsoleRole.Viewer);

        var html = await GetAsync(client, "/attention");

        var section = Section(html, "unresolved");
        Assert.Equal([refunded, old, capture], RowIds(section));
        Assert.Contains("Outcome unknown for 3 h 12 min", section, StringComparison.Ordinal);
        Assert.Contains("A capture: no answer for 40 min", section, StringComparison.Ordinal);
        Assert.Contains("A refund of 75.00 USD: no answer for 5 h", section, StringComparison.Ordinal);
        Assert.Contains("We're checking automatically.", section, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Age_past_twice_the_limit_is_marked_over_limit()
    {
        await db.ResetAsync();
        await using var factory = new DbConsoleFactory(db);
        var now = factory.Clock.GetUtcNow();
        var over = await db.SeedPaymentAsync("replica", "UNKNOWN");
        await SetUpdatedAsync(over, now.AddMinutes(-31));
        var under = await db.SeedPaymentAsync("replica", "UNKNOWN");
        await SetUpdatedAsync(under, now.AddMinutes(-20));
        using var client = Client(factory, ConsoleRole.Viewer);

        var html = await GetAsync(client, "/attention");

        Assert.Single(Regex.Matches(Section(html, "unresolved"), "Over limit"));
    }

    [Fact]
    public async Task Stuck_void_says_handle_it_at_the_psp()
    {
        await db.ResetAsync();
        await using var factory = new DbConsoleFactory(db);
        var payment = await db.SeedPaymentAsync("replica", "AUTHORIZED");
        await db.SeedAttemptAsync("replica", payment, "void", "UNKNOWN", factory.Clock.GetUtcNow().AddHours(-6));
        using var client = Client(factory, ConsoleRole.Operator);

        var html = await GetAsync(client, "/attention");

        Assert.Contains("This console can't settle a stuck void. Handle it at the PSP.", Section(html, "unresolved"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Expiring_lists_authorizations_within_24_hours_soonest_first()
    {
        await db.ResetAsync();
        await using var factory = new DbConsoleFactory(db);
        var now = factory.Clock.GetUtcNow();
        var later = await db.SeedPaymentAsync("replica", "AUTHORIZED");
        await SetAuthorizedUntilAsync(later, now.AddHours(20));
        var sooner = await db.SeedPaymentAsync("replica", "AUTHORIZED");
        await SetAuthorizedUntilAsync(sooner, now.AddHours(9));
        var far = await db.SeedPaymentAsync("replica", "AUTHORIZED");
        await SetAuthorizedUntilAsync(far, now.AddDays(3));
        using var client = Client(factory, ConsoleRole.Viewer);

        var html = await GetAsync(client, "/attention");

        var section = Section(html, "expiring");
        Assert.Equal([sooner, later], RowIds(section));
        Assert.Contains("Ends in 9 h", section, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Orphan_intent_names_the_person_and_the_action()
    {
        await db.ResetAsync();
        await using var factory = new DbConsoleFactory(db);
        var payment = await db.SeedPaymentAsync("replica", "CAPTURED");
        var person = Guid.NewGuid();
        await db.ExecAsync("primary", $"insert into console.staff (id, oidc_subject, email, role) values ('{person}', 'sub-priya', 'priya@example.test', 'operator')");
        await OrphanAsync(payment, person);
        using var client = Client(factory, ConsoleRole.Viewer);

        var html = await GetAsync(client, "/attention");

        var section = Section(html, "orphan");
        Assert.Equal([payment], RowIds(section));
        Assert.Contains("A refund by priya@example.test has no recorded result", section, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Queues_leave_out_other_merchants()
    {
        await db.ResetAsync();
        await using var factory = new DbConsoleFactory(db);
        await db.SeedPaymentAsync("replica", "REVIEW", merchant: ConsoleDb.OtherMerchant);
        using var client = Client(factory, ConsoleRole.Viewer);

        var html = await GetAsync(client, "/attention");

        Assert.Contains("Nothing needs attention.", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Counts_stop_at_1000()
    {
        await db.ResetAsync();
        await using var factory = new DbConsoleFactory(db);
        await db.ExecAsync("replica", $"""
            insert into payments (id, merchant_id, state, amount_minor, currency, captured_minor, expires_at, created_at, updated_at)
            select gen_random_uuid(), '{ConsoleDb.Merchant}', 'REVIEW', 100, 'USD', 0, now() + interval '1 day', now(), now() - (g || ' seconds')::interval
            from generate_series(1, 1001) g
            """);
        using var client = Client(factory, ConsoleRole.Viewer);

        var html = await GetAsync(client, "/attention");

        var section = Section(html, "review");
        Assert.Contains("Needs review, 1,000+", section, StringComparison.Ordinal);
        Assert.Equal(10, RowIds(section).Count);
    }

    [Fact]
    public async Task Replica_unavailable_says_so_and_keeps_the_page()
    {
        await db.ResetAsync();
        await using var factory = new DbConsoleFactory(db, settings =>
            settings["Console:Db:Replica"] = "Host=127.0.0.1;Port=1;Database=unused;Timeout=1");
        using var client = Client(factory, ConsoleRole.Viewer);

        var response = await client.GetAsync("/attention");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await TextAsync(response);
        Assert.Contains("The queues are unavailable because the read replica can't be reached.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Nothing needs attention.", html, StringComparison.Ordinal);
    }

    // N2: roles, badge and navigation.

    [Fact]
    public async Task Viewer_sees_why_they_cannot_propose()
    {
        await db.ResetAsync();
        await using var factory = new DbConsoleFactory(db);
        await db.SeedPaymentAsync("replica", "REVIEW");
        using var client = Client(factory, ConsoleRole.Viewer);

        var html = await GetAsync(client, "/attention");

        Assert.Contains("Proposing needs the Operator role.", Section(html, "review"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Operator_sees_no_note_on_review_but_one_on_approval()
    {
        await db.ResetAsync();
        await using var factory = new DbConsoleFactory(db);
        var payment = await db.SeedPaymentAsync("replica", "REVIEW");
        await ProposeAsync(payment, Guid.NewGuid());
        using var client = Client(factory, ConsoleRole.Operator);

        var html = await GetAsync(client, "/attention");

        Assert.DoesNotContain("data-role-note", Section(html, "review"), StringComparison.Ordinal);
        Assert.Contains("Deciding needs the Approver role.", Section(html, "approval"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Approver_sees_their_own_proposal_marked_and_not_counted()
    {
        await db.ResetAsync();
        await using var factory = new DbConsoleFactory(db);
        var approver = factory.AddStaff(ConsoleRole.Approver);
        var mine = await db.SeedPaymentAsync("replica", "REVIEW");
        await ProposeAsync(mine, approver.Id);
        var theirs = await db.SeedPaymentAsync("replica", "REVIEW");
        await ProposeAsync(theirs, Guid.NewGuid());
        using var client = factory.ClientFor(approver.Subject);

        var html = await GetAsync(client, "/attention");

        var section = Section(html, "approval");
        Assert.Single(Regex.Matches(section, ">Yours<"));
        Assert.Contains("You proposed this, so another Approver decides.", section, StringComparison.Ordinal);
        // Both cases already have a proposal, so only someone else's is left to act on.
        Assert.Equal("1", Badge(html));
    }

    [Theory]
    [InlineData(ConsoleRole.Viewer, null)]
    [InlineData(ConsoleRole.Admin, null)]
    [InlineData(ConsoleRole.Operator, "2")]
    [InlineData(ConsoleRole.Approver, "3")]
    public async Task Badge_counts_what_the_person_can_act_on(ConsoleRole role, string? expected)
    {
        await db.ResetAsync();
        await using var factory = new DbConsoleFactory(db);
        var now = factory.Clock.GetUtcNow();
        // One case with nothing proposed, one waiting for approval, one authorization ending.
        await db.SeedPaymentAsync("replica", "REVIEW");
        var proposed = await db.SeedPaymentAsync("replica", "REVIEW");
        await ProposeAsync(proposed, Guid.NewGuid());
        var expiring = await db.SeedPaymentAsync("replica", "AUTHORIZED");
        await SetAuthorizedUntilAsync(expiring, now.AddHours(2));
        var unknown = await db.SeedPaymentAsync("replica", "UNKNOWN");
        await SetUpdatedAsync(unknown, now.AddHours(-2));
        using var client = Client(factory, role);

        // The badge shows on every page, not only the home page.
        var html = await GetAsync(client, "/Payments");

        Assert.Equal(expected, Badge(html));
    }

    [Fact]
    public async Task Badge_shows_99_plus_above_99()
    {
        await db.ResetAsync();
        await using var factory = new DbConsoleFactory(db);
        await db.ExecAsync("replica", $"""
            insert into payments (id, merchant_id, state, amount_minor, currency, captured_minor, expires_at, created_at, updated_at)
            select gen_random_uuid(), '{ConsoleDb.Merchant}', 'REVIEW', 100, 'USD', 0, now() + interval '1 day', now(), now()
            from generate_series(1, 120)
            """);
        using var client = Client(factory, ConsoleRole.Operator);

        var html = await GetAsync(client, "/attention");

        Assert.Equal("99+", Badge(html));
        Assert.Contains("aria-label=\"Needs attention, 120 to act on\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Navigation_puts_dashboard_first_and_attention_second()
    {
        await db.ResetAsync();
        await using var factory = new DbConsoleFactory(db);
        using var client = Client(factory, ConsoleRole.Viewer);

        var html = await GetAsync(client, "/attention");

        var sidebar = Between(html, "<nav class=\"sidebar\"", "</nav>");
        Assert.Equal(["/", "/attention", "/Payments", "/Audit", "/Staff"], Hrefs(sidebar));
        Assert.Contains("aria-current=\"page\"", Between(sidebar, "href=\"/attention\"", "</a>"), StringComparison.Ordinal);
        var tabs = Between(html, "<nav class=\"tabs\"", "</nav>");
        Assert.Equal(["/", "/attention", "/Payments", "/More"], Hrefs(tabs));
        Assert.Contains("<span>Attention</span>", tabs, StringComparison.Ordinal);
    }

    // The queue reads use their partial indexes.

    public static TheoryData<string, string> QueueIndexes() => new()
    {
        { "review_payments", "payments_review" },
        { "review_attempts", "payment_attempts_review" },
        { "review_refunds", "refunds_review" },
        { "unresolved_payments", "payments_unresolved" },
        { "unresolved_attempts", "payment_attempts_unresolved" },
        { "unresolved_refunds", "refunds_unresolved" },
        { "expiring", "payments_authorized" },
    };

    [Theory]
    [MemberData(nameof(QueueIndexes))]
    public async Task Queue_read_uses_its_partial_index(string read, string index)
    {
        await db.ResetAsync();
        await using var connection = new NpgsqlConnection(db.AdminFor("replica"));
        await connection.OpenAsync();
        await using (var off = new NpgsqlCommand("set enable_seqscan = off", connection))
        {
            await off.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand("explain (format json) " + QueueReader.Sql[read], connection);
        command.Parameters.AddWithValue("merchants", new[] { ConsoleDb.Merchant });
        command.Parameters.AddWithValue("cutoff", DateTimeOffset.UtcNow.AddMinutes(-15));
        command.Parameters.AddWithValue("until", DateTimeOffset.UtcNow.AddHours(24));
        command.Parameters.AddWithValue("limit", 11);
        var plan = JsonDocument.Parse((string)(await command.ExecuteScalarAsync())!).RootElement[0].GetProperty("Plan");

        // Partitions name their copy of an index after the parent's columns and predicate, so
        // match on the parent's index through pg_inherits.
        var used = Walk(plan)
            .Where(node => node.TryGetProperty("Index Name", out _))
            .Select(node => node.GetProperty("Index Name").GetString()!)
            .ToList();
        // Rows join payments by primary key for amount and currency, so other indexes appear too.
        var parents = new List<string>();
        foreach (var name in used.Distinct())
        {
            parents.Add(await ParentIndexAsync(connection, name));
        }

        Assert.Contains(index, parents);
    }

    private async Task<string> ParentIndexAsync(NpgsqlConnection connection, string name)
    {
        await using var command = new NpgsqlCommand(
            """
            select coalesce(
              (select p.relname from pg_class c
                 join pg_inherits i on i.inhrelid = c.oid
                 join pg_class p on p.oid = i.inhparent
                where c.relname = @name and c.relkind = 'i'),
              @name)
            """,
            connection);
        command.Parameters.AddWithValue("name", name);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static HttpClient Client(ConsoleFactory factory, ConsoleRole role) =>
        factory.ClientFor(factory.AddStaff(role).Subject);

    private static async Task<string> GetAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await TextAsync(response);
    }

    private static async Task<string> TextAsync(HttpResponseMessage response) =>
        WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

    private Task SetUpdatedAsync(Guid payment, DateTimeOffset at) =>
        db.ExecAsync("replica", $"update payments set updated_at = '{at:O}' where id = '{payment}'");

    private Task SetAuthorizedUntilAsync(Guid payment, DateTimeOffset at) =>
        db.ExecAsync("replica", $"update payments set authorized_until = '{at:O}' where id = '{payment}'");

    private Task ProposeAsync(Guid payment, Guid proposer) =>
        db.ExecAsync("replica", $"""
            insert into console.review_resolutions (id, payment_id, kind, outcome, psp_answer, evidence, state, proposed_by)
            values ('{Guid.NewGuid()}', '{payment}', 'payment', 'capture', 'Approved', 'PSP record', 'proposed', '{proposer}')
            """);

    // The audit writer judges age by the database clock, as the Audit log page does.
    private Task OrphanAsync(Guid payment, Guid? staff = null) =>
        db.ExecAsync("primary", $"""
            insert into console.audit_log (staff_id, phase, action, payment_id, idempotency_key, reason, created_at)
            values ('{staff ?? Guid.NewGuid()}', 'intent', 'refund', '{payment}', 'key-{Guid.NewGuid():N}', 'r', now() - interval '25 minutes')
            """);

    private static string Section(string html, string queue)
    {
        var match = Regex.Match(html, $"<section[^>]*data-queue=\"{queue}\".*?</section>", RegexOptions.Singleline);
        Assert.True(match.Success, "No section for queue " + queue);
        return match.Value;
    }

    private static List<string> QueueKeys(string html) =>
        [.. QueueAttribute().Matches(html).Select(m => m.Groups[1].Value)];

    private static List<Guid> RowIds(string section) =>
        [.. RowAttribute().Matches(section).Select(m => Guid.Parse(m.Groups[1].Value))];

    private static string? Badge(string html)
    {
        var match = BadgeAttribute().Match(Between(html, "<nav class=\"sidebar\"", "</nav>"));
        return match.Success ? match.Groups[1].Value : null;
    }

    private static List<string> Hrefs(string nav) =>
        [.. Regex.Matches(nav, "<a [^>]*href=\"([^\"]+)\"").Select(m => m.Groups[1].Value)];

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

    [GeneratedRegex("data-queue=\"([a-z]+)\"")]
    private static partial Regex QueueAttribute();

    [GeneratedRegex("data-row=\"([0-9a-f-]{36})\"")]
    private static partial Regex RowAttribute();

    [GeneratedRegex("<span class=\"badge\"[^>]*>([^<]+)</span>")]
    private static partial Regex BadgeAttribute();
}
