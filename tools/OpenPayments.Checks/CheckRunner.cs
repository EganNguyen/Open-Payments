using System.Globalization;
using System.Reflection;
using Npgsql;

namespace OpenPayments.Checks;

public static class CheckRunner
{
    private static readonly string[] Stages = ["phase0", "phase1", "phase2", "phase3"];

    public static async Task<IReadOnlyList<CheckResult>> RunAsync(
        CheckOptions options,
        CancellationToken cancellationToken = default)
    {
        if (Array.IndexOf(Stages, options.Stage) < 0)
        {
            throw new ArgumentException($"Unknown stage '{options.Stage}'.", nameof(options));
        }

        await using var connection = new NpgsqlConnection(NormalizeConnectionString(options.Db));
        await connection.OpenAsync(cancellationToken);
        await PrepareAsync(connection, options.Effects, cancellationToken);

        var results = new List<CheckResult>
        {
            await RunCk1Async(connection, cancellationToken),
            await RunCk2Async(connection, cancellationToken),
            await RunCk3Async(connection, cancellationToken),
            await RunCk4Async(connection, options.Stage, cancellationToken),
            await RunCk5Async(connection, options.Stage, cancellationToken),
            await RunCk6Async(options, cancellationToken),
            await RunCk7Async(connection, options.Stage, cancellationToken),
            await RunCk8Async(connection, options.Stage, cancellationToken),
            await RunCk9Async(connection, options.Stage, cancellationToken),
            await RunCk10Async(connection, options.Stage, cancellationToken),
        };
        return results;
    }

    private static async Task PrepareAsync(
        NpgsqlConnection connection,
        string? effects,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            connection,
            """
            create schema if not exists ck;
            create unlogged table if not exists ck.psp_effects (
              key text not null,
              op text not null,
              amount_minor bigint not null,
              currency text not null,
              at timestamptz not null);
            create table if not exists ck.ledger_rows (
              txn_id uuid not null,
              entry_no smallint not null,
              row_hash text not null,
              primary key (txn_id, entry_no));
            truncate ck.psp_effects;
            """,
            cancellationToken);

        if (string.IsNullOrEmpty(effects) || !File.Exists(effects))
        {
            return;
        }

        using var writer = await connection.BeginTextImportAsync(
            "COPY ck.psp_effects (key, op, amount_minor, currency, at) "
            + "FROM STDIN (FORMAT csv, HEADER true)",
            cancellationToken);
        foreach (var line in File.ReadLines(effects))
        {
            await writer.WriteLineAsync(line);
        }
    }

    private static async Task<CheckResult> RunCk1Async(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        var rows = await ScalarLongAsync(connection, "select count(*) from ledger_entries", cancellationToken);
        if (rows == 0)
        {
            return ZeroRows("CK1", "zero ledger entries");
        }

        var violations = await QueryViolationsAsync(connection, "ck1.sql", cancellationToken);
        if (violations.Count == 0)
        {
            await RememberLedgerRowsAsync(connection, cancellationToken);
        }

        if (await LedgerRowsChangedAsync(connection, cancellationToken))
        {
            violations.Add(new Violation("ledger_manifest", 0));
        }

        return Build("CK1", applicable: true, rows, violations, reason: null);
    }

    private static async Task<CheckResult> RunCk2Async(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        var effects = await ScalarLongAsync(connection, "select count(*) from ck.psp_effects", cancellationToken);
        var payments = await ScalarLongAsync(connection, "select count(*) from payments", cancellationToken);
        var refunds = await ScalarLongAsync(connection, "select count(*) from refunds", cancellationToken);
        if (effects == 0 && payments == 0 && refunds == 0)
        {
            return ZeroRows("CK2", "zero effects, payments and refunds");
        }

        var violations = await QueryViolationsAsync(connection, "ck2.sql", cancellationToken);
        return Build("CK2", applicable: true, effects, violations, reason: null);
    }

    private static async Task<CheckResult> RunCk3Async(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        var payments = await ScalarLongAsync(connection, "select count(*) from payments", cancellationToken);
        if (payments == 0)
        {
            return ZeroRows("CK3", "zero payments");
        }

        var violations = await QueryViolationsAsync(connection, "ck3.sql", cancellationToken);
        return Build("CK3", applicable: true, payments, violations, reason: null);
    }

    private static async Task<CheckResult> RunCk4Async(
        NpgsqlConnection connection,
        string stage,
        CancellationToken cancellationToken)
    {
        if (Rank(stage) < 1)
        {
            return NotApplicable("CK4", "CK4a starts after Phase 1");
        }

        var payments = await ScalarLongAsync(connection, "select count(*) from payments", cancellationToken);
        var outbox = await ScalarLongAsync(connection, "select count(*) from outbox", cancellationToken);
        if (payments + outbox == 0)
        {
            return ZeroRows("CK4", "zero payments and outbox rows");
        }

        var violations = await QueryViolationsAsync(connection, "ck4.sql", cancellationToken);
        if (Rank(stage) >= 3)
        {
            var unpublished = await QueryViolationsAsync(
                connection,
                """
                select id::text, 0::bigint
                from outbox
                where published_at is null and created_at < now() - interval '60 seconds'
                """,
                cancellationToken);
            violations.AddRange(unpublished);
        }

        return Build("CK4", applicable: true, payments + outbox, violations, reason: null);
    }

    private static async Task<CheckResult> RunCk5Async(
        NpgsqlConnection connection,
        string stage,
        CancellationToken cancellationToken)
    {
        if (Rank(stage) < 1)
        {
            return NotApplicable("CK5", "CK5 starts after Phase 1");
        }

        var rows = await ScalarLongAsync(
            connection,
            """
            select (select count(*) from payments)
                 + (select count(*) from payment_attempts)
                 + (select count(*) from refunds)
                 + (select count(*) from psp_events)
            """,
            cancellationToken);
        if (rows == 0)
        {
            return ZeroRows("CK5", "zero payments, attempts, refunds and PSP events");
        }

        var violations = await QueryViolationsAsync(connection, "ck5.sql", cancellationToken);
        return Build("CK5", applicable: true, rows, violations, reason: null);
    }

    private static Task<CheckResult> RunCk6Async(CheckOptions options, CancellationToken cancellationToken)
    {
        if (Rank(options.Stage) < 1)
        {
            return Task.FromResult(NotApplicable("CK6", "CK6 starts after Phase 1"));
        }

        if (string.IsNullOrWhiteSpace(options.Prom))
        {
            return Task.FromResult(NotApplicable("CK6", "CK6 needs --prom"));
        }

        return Ck6Trend.RunAsync(options.Prom, options.From, options.To, options.Stage, cancellationToken);
    }

    private static async Task<CheckResult> RunCk7Async(
        NpgsqlConnection connection,
        string stage,
        CancellationToken cancellationToken)
    {
        if (Rank(stage) < 2)
        {
            return NotApplicable("CK7", "CK7 starts after Phase 2");
        }

        var accounts = await ScalarLongAsync(connection, "select count(*) from accounts", cancellationToken);
        if (accounts == 0)
        {
            return ZeroRows("CK7", "zero accounts");
        }

        var violations = await QueryViolationsAsync(connection, "ck7.sql", cancellationToken);
        return Build("CK7", applicable: true, accounts, violations, reason: null);
    }

    private static async Task<CheckResult> RunCk8Async(
        NpgsqlConnection connection,
        string stage,
        CancellationToken cancellationToken)
    {
        if (Rank(stage) < 2)
        {
            return NotApplicable("CK8", "CK8 starts after Phase 2");
        }

        var lines = await ScalarLongAsync(connection, "select count(*) from settlement_lines", cancellationToken);
        if (lines == 0)
        {
            return ZeroRows("CK8", "zero settlement lines");
        }

        var violations = await QueryViolationsAsync(connection, "ck8.sql", cancellationToken);
        return Build("CK8", applicable: true, lines, violations, reason: null);
    }

    private static async Task<CheckResult> RunCk9Async(
        NpgsqlConnection connection,
        string stage,
        CancellationToken cancellationToken)
    {
        if (Rank(stage) < 1)
        {
            return NotApplicable("CK9", "CK9 starts after Phase 1");
        }

        var payments = await ScalarLongAsync(connection, "select count(*) from payments", cancellationToken);
        if (payments == 0)
        {
            return ZeroRows("CK9", "zero payments");
        }

        var violations = await QueryViolationsAsync(connection, "ck9.sql", cancellationToken);
        return Build("CK9", applicable: true, payments, violations, reason: null);
    }

    private static async Task<CheckResult> RunCk10Async(
        NpgsqlConnection connection,
        string stage,
        CancellationToken cancellationToken)
    {
        if (Rank(stage) < 1)
        {
            return NotApplicable("CK10", "CK10 starts after Phase 1");
        }

        var payments = await ScalarLongAsync(connection, "select count(*) from payments", cancellationToken);
        if (payments == 0)
        {
            return ZeroRows("CK10", "zero payments");
        }

        var violations = await QueryViolationsAsync(connection, "ck10.sql", cancellationToken);
        return Build("CK10", applicable: true, payments, violations, reason: null);
    }

    private static CheckResult Build(
        string check,
        bool applicable,
        long rows,
        List<Violation> violations,
        string? reason)
    {
        var samples = violations
            .Select(violation => violation.SampleId)
            .Where(sample => sample is not null)
            .Select(sample => sample!)
            .Distinct(StringComparer.Ordinal)
            .Take(10)
            .ToList();
        var impact = violations.Sum(violation => violation.ImpactMinor);
        return new CheckResult(
            check,
            applicable,
            applicable && violations.Count == 0,
            rows,
            violations.Count,
            samples,
            impact,
            reason);
    }

    private static CheckResult ZeroRows(string check, string reason) =>
        new(check, true, false, 0, 1, [], 0, reason);

    private static CheckResult NotApplicable(string check, string reason) =>
        new(check, false, false, 0, 0, [], 0, reason);

    private static int Rank(string stage) => Array.IndexOf(Stages, stage);

    private static string NormalizeConnectionString(string value)
    {
        if (!value.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
            && !value.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        var uri = new Uri(value);
        var userInfo = uri.UserInfo.Split(':', 2);
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432,
            Username = Uri.UnescapeDataString(userInfo[0]),
            Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : string.Empty,
            Database = uri.AbsolutePath.TrimStart('/'),
        };

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && parts[0].Equals("sslmode", StringComparison.OrdinalIgnoreCase))
            {
                builder.SslMode = parts[1] switch
                {
                    "disable" => SslMode.Disable,
                    "require" => SslMode.Require,
                    "verify-full" => SslMode.VerifyFull,
                    _ => SslMode.Prefer,
                };
            }
        }

        return builder.ConnectionString;
    }

    private static async Task<List<Violation>> QueryViolationsAsync(
        NpgsqlConnection connection,
        string sqlOrFile,
        CancellationToken cancellationToken)
    {
        var sql = sqlOrFile.EndsWith(".sql", StringComparison.Ordinal)
            ? await ReadSqlAsync(sqlOrFile, cancellationToken)
            : sqlOrFile;
        var violations = new List<Violation>();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var sample = reader.IsDBNull(0) ? null : reader.GetString(0);
            var impact = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
            violations.Add(new Violation(sample, impact));
        }

        return violations;
    }

    private static async Task<string> ReadSqlAsync(string file, CancellationToken cancellationToken)
    {
        var assembly = typeof(CheckRunner).Assembly;
        var name = $"OpenPayments.Checks.sql.{file}";
        await using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing embedded SQL resource {name}.");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<long> ScalarLongAsync(
        NpgsqlConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull
            ? 0
            : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static async Task RememberLedgerRowsAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            connection,
            """
            insert into ck.ledger_rows (txn_id, entry_no, row_hash)
            select e.txn_id,
                   e.entry_no,
                   md5(concat_ws('|',
                     e.txn_id::text, e.entry_no::text, e.merchant_id::text,
                     e.account_id::text, e.subject_id::text, e.direction,
                     e.amount_minor::text, e.currency, e.created_at::text))
            from ledger_entries e
            on conflict (txn_id, entry_no) do nothing
            """,
            cancellationToken);
    }

    private static async Task<bool> LedgerRowsChangedAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        var changed = await ScalarLongAsync(
            connection,
            """
            select count(*)::bigint
            from (
              select 1
              from ck.ledger_rows m
              join ledger_entries e using (txn_id, entry_no)
              where m.row_hash <> md5(concat_ws('|',
                e.txn_id::text, e.entry_no::text, e.merchant_id::text,
                e.account_id::text, e.subject_id::text, e.direction,
                e.amount_minor::text, e.currency, e.created_at::text))
              union all
              select 1
              from ck.ledger_rows m
              where not exists (
                select 1 from ledger_entries e
                where e.txn_id = m.txn_id and e.entry_no = m.entry_no)
            ) changed
            """,
            cancellationToken);
        return changed > 0;
    }

    private sealed record Violation(string? SampleId, long ImpactMinor);
}
