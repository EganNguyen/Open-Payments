using Npgsql;
using Xunit;

namespace OpenPayments.Integration.Tests;

// UNKNOWN and PENDING operations are financial outcomes not yet established. Stage 1 can't
// resolve them, so they must at least be visible: how many, how old and how much, by kind,
// state, currency and PSP. postgres-exporter serves this query as op_unresolved_* gauges.
[Collection("postgres")]
public sealed class UnresolvedMetricsTests(DbFixture db)
{
    [Fact]
    public async Task Exporter_query_counts_ages_and_values_unresolved_operations()
    {
        await db.ResetAsync();
        var seed = await db.SeedMerchantAsync();
        var unknown = await db.SeedPaymentAsync(seed, "UNKNOWN", amountMinor: 1000, operation: "sale", attemptState: "UNKNOWN");
        await db.SeedPaymentAsync(seed, "PSP_PENDING", amountMinor: 500, operation: "authorize", attemptState: "PENDING");
        await db.SeedPaymentAsync(seed, "CAPTURED", amountMinor: 9999, operation: "sale", attemptState: "SUCCEEDED");
        await db.ExecAdminAsync($"""
            update payments set updated_at = now() - interval '5 minutes' where id = '{unknown.PaymentId.Value}';
            update payment_attempts set created_at = now() - interval '5 minutes' where attempt_id = '{unknown.AttemptId.Value}';
            """);

        var rows = await RunAsync(ExporterQuery("op_unresolved"));

        // Settled payments don't appear; each unresolved one does, with its value.
        var unknownPayment = Assert.Single(rows, r => r.Kind == "payment" && r.State == "UNKNOWN");
        Assert.Equal(1, unknownPayment.Count);
        Assert.Equal(1000, unknownPayment.AmountMinor);
        Assert.Equal("USD", unknownPayment.Currency);
        Assert.InRange(unknownPayment.OldestAgeSeconds, 290, 400);
        var pending = Assert.Single(rows, r => r.Kind == "payment" && r.State == "PSP_PENDING");
        Assert.Equal(500, pending.AmountMinor);
        Assert.InRange(pending.OldestAgeSeconds, 0, 60);

        var unknownAttempt = Assert.Single(rows, r => r.Kind == "attempt" && r.State == "UNKNOWN");
        Assert.Equal("mock", unknownAttempt.Psp);
        Assert.Equal(1000, unknownAttempt.AmountMinor);
        Assert.DoesNotContain(rows, r => r.AmountMinor == 9999);
    }

    [Fact]
    public async Task Exporter_query_returns_no_rows_when_everything_is_settled()
    {
        await db.ResetAsync();
        var seed = await db.SeedMerchantAsync();
        await db.SeedPaymentAsync(seed, "CAPTURED", operation: "sale");

        Assert.Empty(await RunAsync(ExporterQuery("op_unresolved")));
    }

    private sealed record Row(string Kind, string State, string Currency, string Psp, double Count, double OldestAgeSeconds, double AmountMinor);

    private async Task<List<Row>> RunAsync(string sql)
    {
        await using var connection = await db.OpenAdminAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<Row>();
        while (await reader.ReadAsync())
        {
            rows.Add(new Row(
                reader.GetString(reader.GetOrdinal("kind")),
                reader.GetString(reader.GetOrdinal("state")),
                reader.GetString(reader.GetOrdinal("currency")).Trim(),
                reader.GetString(reader.GetOrdinal("psp")),
                reader.GetDouble(reader.GetOrdinal("count")),
                reader.GetDouble(reader.GetOrdinal("oldest_age_seconds")),
                reader.GetDouble(reader.GetOrdinal("amount_minor"))));
        }

        return rows;
    }

    /// <summary>The query block under a top-level key of the exporter's queries file, written as a
    /// YAML literal block (<c>query: |</c>).</summary>
    private static string ExporterQuery(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OpenPayments.slnx")))
        {
            directory = directory.Parent;
        }

        var lines = File.ReadAllLines(Path.Combine(directory!.FullName, "deploy", "compose", "prometheus", "postgres-exporter-queries.yaml"));
        var start = Array.FindIndex(lines, line => line == name + ":");
        Assert.True(start >= 0, $"No '{name}' query in the exporter file.");
        var queryLine = Array.FindIndex(lines, start, line => line.Trim() == "query: |");
        var indent = lines[queryLine].IndexOf('q', StringComparison.Ordinal) + 2;
        return string.Join('\n', lines.Skip(queryLine + 1)
            .TakeWhile(line => line.Length == 0 || line.StartsWith(new string(' ', indent), StringComparison.Ordinal))
            .Select(line => line.Length >= indent ? line[indent..] : line));
    }
}
