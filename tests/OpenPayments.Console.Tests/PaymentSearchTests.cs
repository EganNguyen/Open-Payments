using System.Net;
using System.Text.Json;
using Npgsql;
using OpenPayments.Console.Data;
using OpenPayments.Console.Security;
using Xunit;

namespace OpenPayments.Console.Tests;

// Task C8: the read store and the Payments page.
[Collection("console-db")]
public sealed class PaymentSearchTests(ConsoleDb db)
{
    [Fact]
    public async Task Uuid_found_only_on_primary_opens_the_payment()
    {
        await db.ResetAsync();
        var id = await db.SeedPaymentAsync("primary");
        await using var factory = new DbConsoleFactory(db);
        using var client = Viewer(factory);

        var response = await client.GetAsync("/Payments?Q=" + id);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Payments/" + id.ToString("D"), response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Range_over_31_days_is_refused_with_a_message()
    {
        await db.ResetAsync();
        await using var factory = new DbConsoleFactory(db);
        using var client = Viewer(factory);

        var html = await GetTextAsync(client, "/Payments?From=2026-09-01&To=2026-10-15");

        Assert.Contains("Choose a range of 31 days or less.", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Default_range_is_7_days()
    {
        await db.ResetAsync();
        await using var factory = new DbConsoleFactory(db);
        var now = factory.Clock.GetUtcNow();
        var inside = await db.SeedPaymentAsync("replica", createdAt: now.AddDays(-6));
        var outside = await db.SeedPaymentAsync("replica", createdAt: now.AddDays(-8));
        using var client = Viewer(factory);

        var html = await GetTextAsync(client, "/Payments");

        Assert.Contains("value=\"" + now.AddDays(-6).ToString("yyyy-MM-dd") + "\"", html, StringComparison.Ordinal);
        Assert.Contains(inside.ToString("D"), html, StringComparison.Ordinal);
        Assert.DoesNotContain(outside.ToString("D"), html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_query_uses_payments_merchant_created()
    {
        await db.ResetAsync();
        await using var connection = new NpgsqlConnection(db.AdminFor("replica"));
        await connection.OpenAsync();
        // On near-empty partitions a sequential scan is the right plan; disabling it shows
        // whether the index serves the query.
        await using (var off = new NpgsqlCommand("set enable_seqscan = off", connection))
        {
            await off.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand("explain (format json) " + ConsoleReadStore.ListSql, connection);
        command.Parameters.AddWithValue("merchants", new[] { ConsoleDb.Merchant });
        command.Parameters.AddWithValue("from", DateTimeOffset.UtcNow.AddDays(-7));
        command.Parameters.AddWithValue("to", DateTimeOffset.UtcNow);
        command.Parameters.Add(new NpgsqlParameter<string?>("state", null) { DataTypeName = "text" });
        command.Parameters.Add(new NpgsqlParameter<long?>("min", null) { DataTypeName = "bigint" });
        command.Parameters.Add(new NpgsqlParameter<long?>("max", null) { DataTypeName = "bigint" });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset?>("after_at", null) { DataTypeName = "timestamptz" });
        command.Parameters.Add(new NpgsqlParameter<Guid?>("after_id", null) { DataTypeName = "uuid" });
        command.Parameters.AddWithValue("limit", 51);
        var plan = JsonDocument.Parse((string)(await command.ExecuteScalarAsync())!).RootElement[0].GetProperty("Plan");

        var nodes = Walk(plan).ToList();
        Assert.Contains(nodes, node => node.TryGetProperty("Index Name", out var name) && name.GetString()!.Contains("merchant_id_created_at", StringComparison.Ordinal));
        Assert.DoesNotContain(nodes, node => node.GetProperty("Node Type").GetString() == "Seq Scan");
    }

    [Fact]
    public async Task Paging_returns_every_row_once_with_equal_created_at()
    {
        await db.ResetAsync();
        var at = DateTimeOffset.UtcNow.AddHours(-1);
        var seeded = new HashSet<Guid>();
        for (var i = 0; i < 7; i++)
        {
            seeded.Add(await db.SeedPaymentAsync("replica", createdAt: at));
        }

        await using var replica = NpgsqlDataSource.Create(db.As(ConsoleDb.ReadUser, "replica"));
        var store = new ConsoleReadStore(replica, replica);
        var seen = new List<Guid>();
        string? cursor = null;
        do
        {
            var page = await store.ListPaymentsAsync(
                new PaymentQuery([ConsoleDb.Merchant], at.AddDays(-1), at.AddDays(1), null, null, null, cursor, Limit: 3),
                CancellationToken.None);
            seen.AddRange(page.Rows.Select(row => row.Id));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        Assert.Equal(7, seen.Count);
        Assert.Equal(seeded, seen.ToHashSet());
    }

    [Fact]
    public async Task Merchants_outside_the_deployment_are_not_returned()
    {
        await db.ResetAsync();
        await using var factory = new DbConsoleFactory(db);
        var ours = await db.SeedPaymentAsync("replica");
        var theirs = await db.SeedPaymentAsync("replica", merchant: ConsoleDb.OtherMerchant);
        var theirsOnPrimary = await db.SeedPaymentAsync("primary", merchant: ConsoleDb.OtherMerchant);
        using var client = Viewer(factory);

        var html = await GetTextAsync(client, "/Payments");
        Assert.Contains(ours.ToString("D"), html, StringComparison.Ordinal);
        Assert.DoesNotContain(theirs.ToString("D"), html, StringComparison.Ordinal);

        // And a pasted ID from another merchant doesn't open either.
        var lookup = await client.GetAsync("/Payments?Q=" + theirsOnPrimary);
        Assert.Equal(HttpStatusCode.OK, lookup.StatusCode);
        Assert.Contains("No payment has that ID.", System.Net.WebUtility.HtmlDecode(await lookup.Content.ReadAsStringAsync()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lag_over_60s_shows_data_as_of_banner()
    {
        await db.ResetAsync();
        await using var factory = new DbConsoleFactory(db)
        {
            DecorateReadStore = inner => new FixedLag(inner, TimeSpan.FromSeconds(75)),
        };
        using var client = Viewer(factory);

        var html = await GetTextAsync(client, "/Payments");

        var asOf = factory.Clock.GetUtcNow().AddSeconds(-75).UtcDateTime.ToString("HH:mm:ss");
        Assert.Contains("Data as of " + asOf + " UTC", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lag_under_60s_shows_no_banner()
    {
        await db.ResetAsync();
        await using var factory = new DbConsoleFactory(db)
        {
            DecorateReadStore = inner => new FixedLag(inner, TimeSpan.FromSeconds(45)),
        };
        using var client = Viewer(factory);

        Assert.DoesNotContain("Data as of", await GetTextAsync(client, "/Payments"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Replica_down_keeps_id_lookup_working_and_explains_search()
    {
        await db.ResetAsync();
        var id = await db.SeedPaymentAsync("primary");
        await using var factory = new DbConsoleFactory(db, settings =>
            settings["Console:Db:Replica"] = "Host=127.0.0.1;Port=1;Database=replica;Username=x;Password=x;Timeout=1");
        using var client = Viewer(factory);

        var html = await GetTextAsync(client, "/Payments");
        Assert.Contains("Search is unavailable", html, StringComparison.Ordinal);
        Assert.Contains("Looking up a payment by ID still works.", html, StringComparison.Ordinal);

        var lookup = await client.GetAsync("/Payments?Q=" + id);
        Assert.Equal(HttpStatusCode.Redirect, lookup.StatusCode);
        Assert.Equal("/Payments/" + id.ToString("D"), lookup.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Statement_timeout_says_narrow_the_range()
    {
        await db.ResetAsync();
        await using var factory = new DbConsoleFactory(db, settings => settings["Console:Db:StatementTimeoutMs"] = "300");
        using var client = Viewer(factory);

        // Hold a lock the list query must wait for, so the timeout fires however fast the query is.
        await using var blocker = new NpgsqlConnection(db.AdminFor("replica"));
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using (var lockTable = new NpgsqlCommand("lock table payments in access exclusive mode", blocker, transaction))
        {
            await lockTable.ExecuteNonQueryAsync();
        }

        var html = await GetTextAsync(client, "/Payments");
        await transaction.RollbackAsync();

        Assert.Contains("Narrow the date range", html, StringComparison.Ordinal);
    }

    private static HttpClient Viewer(ConsoleFactory factory) =>
        factory.ClientFor(factory.AddStaff(ConsoleRole.Viewer).Subject);

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

    private sealed class FixedLag(IConsoleReadStore inner, TimeSpan lag) : IConsoleReadStore
    {
        public Task<PaymentRow?> FindPaymentAsync(Guid id, ReadFrom from, CancellationToken ct) => inner.FindPaymentAsync(id, from, ct);

        public Task<PaymentPage> ListPaymentsAsync(PaymentQuery query, CancellationToken ct) => inner.ListPaymentsAsync(query, ct);

        public Task<TimeSpan?> ReplicaLagAsync(CancellationToken ct) => Task.FromResult<TimeSpan?>(lag);
    }

    private static async Task<string> GetTextAsync(HttpClient client, string url) =>
        System.Net.WebUtility.HtmlDecode(await client.GetStringAsync(url));
}
