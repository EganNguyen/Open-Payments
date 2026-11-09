// Deadlock recovery lab. Two accounts, 100 concurrent transfers in opposite directions,
// run through the production TxRunner. "unordered" locks the source account first, so
// A->B and B->A transfers take the two rows in opposite orders and deadlock. "ordered"
// locks both rows by id first, so every transaction takes them in the same order.
//
// Usage: dotnet run -- <connection string> [rounds]
using System.Diagnostics;
using System.Text.Json;
using Npgsql;
using OpenPayments.Domain;
using OpenPayments.Transitions;

var connectionString = args.Length > 0
    ? args[0]
    : "Host=127.0.0.1;Port=35432;Username=postgres;Password=lab;Database=postgres";
var rounds = args.Length > 1 ? int.Parse(args[1]) : 5;

var builder = new NpgsqlConnectionStringBuilder(connectionString)
{
    CommandTimeout = 0, // TxRunner requires it, so COMMIT is never cancelled (D28).
    MaxPoolSize = 120,
};
connectionString = builder.ConnectionString;

const int Transfers = 100;
const long OpeningBalance = 10_000;
const int ClientResubmits = 5;
var merchant = new MerchantId(Guid.Parse("00000000-0000-7000-8000-00000000d1ab"));

await using (var admin = new NpgsqlConnection(connectionString))
{
    await admin.OpenAsync();
    await Exec(admin, """
        drop table if exists lab_ledger, lab_transfers, lab_accounts;
        create table lab_accounts (
          id            int primary key,
          balance_minor bigint not null check (balance_minor >= 0));
        create table lab_transfers (
          key          uuid primary key,
          from_id      int not null references lab_accounts(id),
          to_id        int not null references lab_accounts(id),
          amount_minor bigint not null check (amount_minor > 0),
          check (from_id <> to_id));
        create table lab_ledger (
          transfer_key uuid not null references lab_transfers(key),
          account_id   int not null references lab_accounts(id),
          amount_minor bigint not null,
          primary key (transfer_key, account_id));
        """);
}

var report = new List<RoundResult>();
var BusyReasons = new System.Collections.Concurrent.ConcurrentDictionary<string, int>(StringComparer.Ordinal);
foreach (var mode in new[] { Mode.Unordered, Mode.Ordered })
{
    for (var round = 1; round <= rounds; round++)
    {
        report.Add(await RunRoundAsync(mode, round));
    }
}

Console.WriteLine();
Console.WriteLine("| Mode | Rounds | Wall ms (median) | p50 ms | p99 ms | Max ms | Deadlocks (server) | 40P01 retries | Client resubmits | Committed | Invariants |");
Console.WriteLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|");
foreach (var group in report.GroupBy(r => r.Mode))
{
    var rows = group.ToList();
    Console.WriteLine(
        $"| {group.Key} | {rows.Count} | {Median(rows.Select(r => r.WallMs))} | {Median(rows.Select(r => r.P50Ms))} | " +
        $"{Median(rows.Select(r => r.P99Ms))} | {rows.Max(r => r.MaxMs)} | {rows.Sum(r => r.ServerDeadlocks)} | " +
        $"{rows.Sum(r => r.Retries40P01)} | {rows.Sum(r => r.ClientResubmits)} | {rows.Sum(r => r.Committed)} | " +
        $"{(rows.All(r => r.InvariantsHold) ? "all hold" : "VIOLATED")} |");
}

File.WriteAllText("lab-result.json", JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
return report.All(r => r.InvariantsHold) ? 0 : 1;

async Task<RoundResult> RunRoundAsync(Mode mode, int round)
{
    await using (var admin = new NpgsqlConnection(connectionString))
    {
        await admin.OpenAsync();
        await Exec(admin, $"""
            truncate lab_ledger, lab_transfers, lab_accounts;
            insert into lab_accounts values (1, {OpeningBalance}), (2, {OpeningBalance});
            """);
    }

    BusyReasons.Clear();
    var deadlocksBefore = await ServerDeadlocksAsync();
    var metrics = new TransitionMetrics();
    await using var runner = new TxRunner(connectionString, metrics: metrics);

    // 50 A->B and 50 B->A, interleaved, with fixed amounts so every round is the same work.
    var random = new Random(round);
    var plan = Enumerable.Range(0, Transfers)
        .Select(i => new Transfer(Guid.NewGuid(), i % 2 == 0 ? 1 : 2, i % 2 == 0 ? 2 : 1, random.Next(1, 500)))
        .ToList();
    // One transfer larger than any balance: it must be refused, never retried, and leave no trace.
    plan.Add(new Transfer(Guid.NewGuid(), 1, 2, OpeningBalance * 10));

    var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var wall = Stopwatch.StartNew();
    var tasks = plan.Select(async transfer =>
    {
        await start.Task;
        return await SubmitAsync(runner, transfer, mode);
    }).ToList();
    start.SetResult();
    var outcomes = await Task.WhenAll(tasks);
    wall.Stop();

    // Replay ten committed transfers with their original keys: each must be a no-op.
    var replays = await Task.WhenAll(outcomes.Where(o => o.Result == Result.Committed).Take(10)
        .Select(o => SubmitAsync(runner, o.Transfer, mode)));

    var deadlocks = await ServerDeadlocksAsync() - deadlocksBefore;
    var invariants = await CheckInvariantsAsync(outcomes, replays);
    var latencies = outcomes.Select(o => o.ElapsedMs).Order().ToArray();

    var result = new RoundResult(
        mode.ToString().ToLowerInvariant(),
        round,
        wall.ElapsedMilliseconds,
        Percentile(latencies, 0.50),
        Percentile(latencies, 0.99),
        latencies[^1],
        deadlocks,
        metrics.Retries.GetValueOrDefault("40P01"),
        metrics.Retries.GetValueOrDefault("40001"),
        outcomes.Sum(o => o.Resubmits),
        outcomes.Count(o => o.Result == Result.Committed),
        outcomes.Count(o => o.Result == Result.Refused),
        outcomes.Count(o => o.Result == Result.GaveUp),
        replays.Count(o => o.Result == Result.Replayed),
        invariants.Holds,
        invariants.Detail,
        string.Join(", ", BusyReasons.OrderBy(p => p.Key).Select(p => $"{p.Key} {p.Value}")));
    Console.WriteLine(
        $"{result.Mode,-9} round {round}: wall {result.WallMs} ms, p99 {result.P99Ms} ms, server deadlocks {deadlocks}, " +
        $"40P01 retries {result.Retries40P01}, resubmits {result.ClientResubmits}, committed {result.Committed}, " +
        $"refused {result.Refused}, gave up {result.GaveUp}, busy [{result.BusyReasons}], replays no-op {result.ReplaysNoOp}/10, invariants {(invariants.Holds ? "hold" : "VIOLATED: " + invariants.Detail)}");
    return result;
}

// The client side of the 503 contract: TxRunner retries a deadlock at most twice, then
// throws BusyException; the client resubmits with the same idempotency key.
async Task<Outcome> SubmitAsync(TxRunner runner, Transfer transfer, Mode mode)
{
    var clock = Stopwatch.StartNew();
    for (var resubmit = 0; ; resubmit++)
    {
        try
        {
            var result = await runner.RunAsync(merchant, (connection, transaction, ct) =>
                ApplyAsync(connection, transaction, transfer, mode, ct), CancellationToken.None);
            return new Outcome(transfer, result, resubmit, clock.ElapsedMilliseconds);
        }
        catch (BusyException busy) when (resubmit < ClientResubmits)
        {
            BusyReasons.AddOrUpdate(busy.Reason, 1, static (_, n) => n + 1);
            await Task.Delay(Random.Shared.Next(10, 50 * (resubmit + 1)));
        }
        catch (BusyException busy)
        {
            BusyReasons.AddOrUpdate(busy.Reason, 1, static (_, n) => n + 1);
            return new Outcome(transfer, Result.GaveUp, resubmit, clock.ElapsedMilliseconds);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.CheckViolation)
        {
            return new Outcome(transfer, Result.Refused, resubmit, clock.ElapsedMilliseconds);
        }
    }
}

static async Task<Result> ApplyAsync(
    NpgsqlConnection connection, NpgsqlTransaction transaction, Transfer transfer, Mode mode, CancellationToken ct)
{
    // The idempotency record is written in the same transaction as the money, so a retry or a
    // resubmit can't apply a transfer twice.
    await using (var claim = new NpgsqlCommand(
        "insert into lab_transfers (key, from_id, to_id, amount_minor) values (@key, @from, @to, @amount) on conflict (key) do nothing",
        connection, transaction))
    {
        claim.Parameters.AddWithValue("key", transfer.Key);
        claim.Parameters.AddWithValue("from", transfer.From);
        claim.Parameters.AddWithValue("to", transfer.To);
        claim.Parameters.AddWithValue("amount", transfer.Amount);
        if (await claim.ExecuteNonQueryAsync(ct) == 0)
        {
            return Result.Replayed;
        }
    }

    if (mode == Mode.Ordered)
    {
        // The fix: take both row locks up front, lowest id first, whatever the direction.
        // FOR NO KEY UPDATE is the lock UPDATE itself takes. Plain FOR UPDATE would conflict
        // with the FOR KEY SHARE locks every lab_transfers insert takes on both accounts for
        // its foreign keys, and the "fixed" order would deadlock through them.
        await using var lockRows = new NpgsqlCommand(
            "select id from lab_accounts where id = any(@ids) order by id for no key update", connection, transaction);
        lockRows.Parameters.AddWithValue("ids", new[] { transfer.From, transfer.To });
        await using var reader = await lockRows.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
        }
    }

    // Unordered: the debit locks the source row, then the credit wants the destination row.
    // An A->B and a B->A transfer then each hold the row the other needs.
    await Update(connection, transaction, transfer.From, -transfer.Amount, ct);
    await Pause(connection, transaction, ct); // widens the window equally in both modes
    await Update(connection, transaction, transfer.To, transfer.Amount, ct);

    await using (var ledger = new NpgsqlCommand(
        "insert into lab_ledger values (@key, @from, -@amount), (@key, @to, @amount)", connection, transaction))
    {
        ledger.Parameters.AddWithValue("key", transfer.Key);
        ledger.Parameters.AddWithValue("from", transfer.From);
        ledger.Parameters.AddWithValue("to", transfer.To);
        ledger.Parameters.AddWithValue("amount", transfer.Amount);
        await ledger.ExecuteNonQueryAsync(ct);
    }

    return Result.Committed;
}

static async Task Update(NpgsqlConnection connection, NpgsqlTransaction transaction, int account, long delta, CancellationToken ct)
{
    // balance_minor >= 0 is a check constraint, so an overdraft fails with 23514 and rolls back.
    await using var command = new NpgsqlCommand(
        "update lab_accounts set balance_minor = balance_minor + @delta where id = @id", connection, transaction);
    command.Parameters.AddWithValue("delta", delta);
    command.Parameters.AddWithValue("id", account);
    await command.ExecuteNonQueryAsync(ct);
}

static async Task Pause(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
{
    await using var command = new NpgsqlCommand("select pg_sleep(0.005)", connection, transaction);
    await command.ExecuteNonQueryAsync(ct);
}

async Task<(bool Holds, string Detail)> CheckInvariantsAsync(Outcome[] outcomes, Outcome[] replays)
{
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();
    var failures = new List<string>();

    var total = await Scalar(connection, "select sum(balance_minor) from lab_accounts");
    if (total != 2 * OpeningBalance)
    {
        failures.Add($"lost money: total {total}, expected {2 * OpeningBalance}");
    }

    if (await Scalar(connection, "select count(*) from lab_accounts where balance_minor < 0") != 0)
    {
        failures.Add("negative balance");
    }

    var committed = outcomes.Count(o => o.Result == Result.Committed);
    var transfers = await Scalar(connection, "select count(*) from lab_transfers");
    if (transfers != committed)
    {
        failures.Add($"duplicate or missing transfers: {transfers} rows, {committed} committed");
    }

    if (replays.Any(r => r.Result != Result.Replayed))
    {
        failures.Add("a replayed key applied again");
    }

    var unbalanced = await Scalar(connection, """
        select count(*) from (
          select t.key from lab_transfers t left join lab_ledger l on l.transfer_key = t.key
          group by t.key, t.amount_minor
          having count(l.*) <> 2 or coalesce(sum(l.amount_minor), 1) <> 0
             or max(l.amount_minor) <> t.amount_minor) bad
        """);
    if (unbalanced != 0)
    {
        failures.Add($"{unbalanced} transfers without exactly two balancing ledger entries");
    }

    var drift = await Scalar(connection, $"""
        select count(*) from lab_accounts a
        where a.balance_minor <> {OpeningBalance} + coalesce(
          (select sum(amount_minor) from lab_ledger l where l.account_id = a.id), 0)
        """);
    if (drift != 0)
    {
        failures.Add("an account balance disagrees with its ledger");
    }

    return (failures.Count == 0, string.Join("; ", failures));
}

async Task<long> ServerDeadlocksAsync()
{
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();
    // The statistics collector flushes asynchronously; force a fresh snapshot.
    await Exec(connection, "select pg_stat_force_next_flush()");
    await Exec(connection, "select pg_stat_clear_snapshot()");
    return await Scalar(connection, "select deadlocks from pg_stat_database where datname = current_database()");
}

static async Task Exec(NpgsqlConnection connection, string sql)
{
    await using var command = new NpgsqlCommand(sql, connection);
    await command.ExecuteNonQueryAsync();
}

static async Task<long> Scalar(NpgsqlConnection connection, string sql)
{
    await using var command = new NpgsqlCommand(sql, connection);
    return Convert.ToInt64(await command.ExecuteScalarAsync());
}

static long Percentile(long[] sorted, double p) =>
    sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(p * sorted.Length) - 1)];

static long Median(IEnumerable<long> values)
{
    var sorted = values.Order().ToArray();
    return sorted[sorted.Length / 2];
}

enum Mode { Unordered, Ordered }

enum Result { Committed, Replayed, Refused, GaveUp }

sealed record Transfer(Guid Key, int From, int To, long Amount);

sealed record Outcome(Transfer Transfer, Result Result, int Resubmits, long ElapsedMs);

sealed record RoundResult(
    string Mode, int Round, long WallMs, long P50Ms, long P99Ms, long MaxMs,
    long ServerDeadlocks, long Retries40P01, long Retries40001, int ClientResubmits,
    int Committed, int Refused, int GaveUp, int ReplaysNoOp, bool InvariantsHold, string Detail, string BusyReasons);
