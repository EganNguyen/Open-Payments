using System.Globalization;
using Npgsql;
using OpenPayments.Console.Presentation;

namespace OpenPayments.Console.Data;

/// <summary>One row of the lifecycle snapshot: a payment state and how many recent payments are in it.</summary>
public sealed record LifecycleState(string State, long Count, DateTimeOffset Oldest);

/// <summary>A live invariant the console can check cheaply on the replica.</summary>
public sealed record CorrectnessCheck(string Key, string Label, string Help, long Issues);

/// <summary>
/// Operational data for the home dashboard, all read from the replica. A read that times out
/// marks its own section unavailable instead of taking the page down; an unreachable replica
/// still throws <see cref="ReplicaUnavailableException"/>.
/// </summary>
public sealed record DashboardSnapshot(
    IReadOnlyList<LifecycleState> Lifecycle,
    long? PendingRefunds,
    long? ActiveAuthorizations,
    long? StuckOperations,
    IReadOnlyList<CorrectnessCheck> Correctness,
    bool LifecycleUnavailable,
    bool CorrectnessUnavailable);

public interface IDashboardReader
{
    Task<DashboardSnapshot> ReadAsync(DateTimeOffset now, CancellationToken ct);
}

/// <summary>Dashboard settings, all configuration.</summary>
public sealed class DashboardOptions
{
    /// <summary>How far back the lifecycle panel looks. Bounded so the dashboard stays cheap.</summary>
    public TimeSpan LifecycleWindow { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>How long an operation may wait before it counts as stuck. Matches CK5.</summary>
    public TimeSpan StuckAfter { get; set; } = TimeSpan.FromSeconds(60);
}

public sealed class DashboardReader(
    NpgsqlDataSource replica,
    Microsoft.Extensions.Options.IOptions<DashboardOptions> options,
    Microsoft.Extensions.Options.IOptions<SearchOptions> search) : IDashboardReader
{
    // Each read is bounded by merchant and, for lifecycle, by created_at. The lifecycle query
    // uses payments_merchant_created; the others use the partial and payment indexes.
    public const string LifecycleSql = """
        select state, count(*)::bigint, min(created_at)
          from payments
         where merchant_id = any(@merchants) and created_at >= @from
         group by state
        """;

    public const string SummarySql = """
        select
          (select count(*)::bigint from refunds
            where merchant_id = any(@merchants) and state in ('PENDING','UNKNOWN')),
          (select count(*)::bigint from payments
            where merchant_id = any(@merchants) and state = 'AUTHORIZED')
        """;

    // The three live correctness checks, plus the count of operations that have waited past
    // the stuck limit. They mirror CK3, CK9, CK10 and CK5 without reading psp_events, which
    // the console's read role can't see.
    public const string CorrectnessSql = """
        select
          (select count(*)::bigint from (
             select 1 from payments p
              where p.merchant_id = any(@merchants)
                and p.refunded_minor + p.refund_pending_minor > p.captured_minor
             union all
             select 1 from payments p
              left join (select payment_id, sum(amount_minor) as unresolved
                           from refunds where state in ('PENDING','UNKNOWN') group by payment_id) r
                on r.payment_id = p.id
              where p.merchant_id = any(@merchants)
                and p.refund_pending_minor <> coalesce(r.unresolved, 0)
           ) refund_totals),
          (select count(*)::bigint from (
             select 1 from payments p
              where p.merchant_id = any(@merchants)
                and p.state in ('CAPTURED','PARTIALLY_REFUNDED','REFUNDED','SETTLED')
                and not exists (select 1 from ledger_transactions t
                                 where t.payment_id = p.id and t.kind = 'capture')
             union all
             select 1 from ledger_transactions t
              join payments p on p.id = t.payment_id
              where t.merchant_id = any(@merchants) and t.kind = 'capture'
                and p.state not in ('CAPTURED','PARTIALLY_REFUNDED','REFUNDED','SETTLED')
           ) capture_ledger),
          (select count(*)::bigint from (
             select 1 from payments p
              left join (
                select t.payment_id,
                       sum(case e.direction when 'D' then e.amount_minor else -e.amount_minor end)::bigint as total
                  from ledger_transactions t
                  join ledger_entries e on e.txn_id = t.id
                  join accounts a on a.id = e.account_id
                 where t.kind = 'capture' and a.account_type = 'psp_receivable'
                 group by t.payment_id
              ) l on l.payment_id = p.id
              where p.merchant_id = any(@merchants)
                and p.captured_minor <> coalesce(l.total, 0)
           ) capture_amount),
          (select count(*)::bigint from (
             select 1 from payments p
              where p.merchant_id = any(@merchants)
                and p.state in ('PSP_PENDING','UNKNOWN') and p.updated_at < @stuck_before
             union all
             select 1 from payment_attempts a
              where a.merchant_id = any(@merchants)
                and a.state in ('PENDING','UNKNOWN') and a.created_at < @stuck_before
             union all
             select 1 from refunds r
              where r.merchant_id = any(@merchants)
                and r.state in ('PENDING','UNKNOWN') and r.created_at < @stuck_before
           ) stuck)
        """;

    public async Task<DashboardSnapshot> ReadAsync(DateTimeOffset now, CancellationToken ct)
    {
        var settings = options.Value;
        var merchants = search.Value.Merchants.ToArray();
        var lifecycle = new List<LifecycleState>();
        var lifecycleUnavailable = false;
        long? pendingRefunds = null;
        long? activeAuthorizations = null;
        long? stuck = null;
        var correctness = new List<CorrectnessCheck>();
        var correctnessUnavailable = false;

        try
        {
            await using var connection = await replica.OpenConnectionAsync(ct);

            try
            {
                await using var command = new NpgsqlCommand(LifecycleSql, connection);
                command.Parameters.AddWithValue("merchants", merchants);
                command.Parameters.AddWithValue("from", now - settings.LifecycleWindow);
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    lifecycle.Add(new LifecycleState(
                        reader.GetString(0),
                        reader.GetInt64(1),
                        reader.GetFieldValue<DateTimeOffset>(2)));
                }
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.QueryCanceled)
            {
                lifecycleUnavailable = true;
            }

            try
            {
                await using var command = new NpgsqlCommand(SummarySql, connection);
                command.Parameters.AddWithValue("merchants", merchants);
                await using var reader = await command.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    pendingRefunds = reader.GetInt64(0);
                    activeAuthorizations = reader.GetInt64(1);
                }
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.QueryCanceled)
            {
                // Leave both null; the view says the summary is unavailable.
            }

            try
            {
                await using var command = new NpgsqlCommand(CorrectnessSql, connection);
                command.Parameters.AddWithValue("merchants", merchants);
                command.Parameters.AddWithValue("stuck_before", now - settings.StuckAfter);
                await using var reader = await command.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    correctness =
                    [
                        new("refund_totals", "Refund totals",
                            "Refunded plus pending never exceeds the captured amount.", reader.GetInt64(0)),
                        new("capture_ledger", "Capture records",
                            "Every captured payment has a capture ledger transaction.", reader.GetInt64(1)),
                        new("capture_amount", "Capture amounts",
                            "The capture ledger total matches the payment's captured amount.", reader.GetInt64(2)),
                    ];
                    stuck = reader.GetInt64(3);
                }
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.QueryCanceled)
            {
                correctnessUnavailable = true;
            }
        }
        catch (Exception exception) when (IsUnreachable(exception))
        {
            throw new ReplicaUnavailableException(exception);
        }

        return new DashboardSnapshot(
            [.. lifecycle.OrderBy(row => LifecycleOrder(row.State)).ThenBy(row => row.State, StringComparer.Ordinal)],
            pendingRefunds,
            activeAuthorizations,
            stuck,
            correctness,
            lifecycleUnavailable,
            correctnessUnavailable);
    }

    /// <summary>Lifecycle order: actionable and unresolved first, then the settled outcomes.</summary>
    public static int LifecycleOrder(string state) => state switch
    {
        "REVIEW" => 0,
        "UNKNOWN" => 1,
        "PSP_PENDING" => 2,
        "ACTION_REQUIRED" => 3,
        "AUTHORIZED" => 4,
        "CAPTURED" => 5,
        "PARTIALLY_REFUNDED" => 6,
        "REFUNDED" => 7,
        "CANCELED" => 8,
        "VOIDED" => 9,
        "EXPIRED" => 10,
        "AWAITING_METHOD" => 11,
        _ => 12,
    };

    private static bool IsUnreachable(Exception exception) =>
        exception is NpgsqlException { IsTransient: true } or System.Net.Sockets.SocketException or TimeoutException
        || exception is NpgsqlException { InnerException: System.Net.Sockets.SocketException or TimeoutException or IOException };
}
