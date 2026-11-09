using Npgsql;
using OpenPayments.Console.Security;

namespace OpenPayments.Console.Data;

/// <summary>The five Needs attention queues, in the order the page shows them.</summary>
public enum Queue
{
    Review,
    Approval,
    Orphan,
    Unresolved,
    Expiring,
}

/// <summary>
/// One row in a queue. <see cref="Subject"/> says what needs attention: "payment",
/// "attempt:capture", "refund" and so on. <see cref="At"/> is when it started waiting, or for
/// an expiring authorization, when its window ends.
/// </summary>
public sealed record AttentionRow(
    Queue Queue,
    Guid PaymentId,
    Guid MerchantId,
    string Subject,
    long AmountMinor,
    string Currency,
    DateTimeOffset At,
    string? Detail,
    Guid? Actor,
    string? ActorEmail,
    bool Proposed);

public sealed record QueueView(Queue Queue, IReadOnlyList<AttentionRow> Rows, int Count);

public sealed record AttentionQueues(IReadOnlyList<QueueView> Queues, TimeSpan Lag);

public interface IQueueReader
{
    /// <summary>Every queue, read from the replica. Throws <see cref="ReplicaUnavailableException"/>.</summary>
    Task<AttentionQueues> ReadAsync(DateTimeOffset now, CancellationToken ct);

    /// <summary>Rows this person can act on, by role. Zero for a role with no money action.</summary>
    Task<int> CountActionableAsync(Staff staff, DateTimeOffset now, CancellationToken ct);
}

/// <summary>Needs attention settings, all configuration.</summary>
public sealed class AttentionOptions
{
    /// <summary>A starting value: no age limit is set in the design docs. CK5's 60 s is the
    /// reconciler's check, which is too short for a person to look.</summary>
    public TimeSpan UnresolvedAfter { get; set; } = TimeSpan.FromMinutes(15);

    public TimeSpan ExpiringWithin { get; set; } = TimeSpan.FromHours(24);

    public int RowsPerQueue { get; set; } = 10;

    /// <summary>Counts stop here, so every count query is bounded.</summary>
    public const int CountCap = 1000;
}

public sealed class QueueReader(
    NpgsqlDataSource replica,
    NpgsqlDataSource primary,
    IConsoleReadStore readStore,
    IAuditWriter audit,
    Microsoft.Extensions.Options.IOptions<AttentionOptions> options,
    Microsoft.Extensions.Options.IOptions<SearchOptions> search,
    Microsoft.Extensions.Options.IOptions<Pages.Audit.AuditOptions> auditOptions) : IQueueReader
{
    // Each read names its state as a literal, so the partial index matches (0003_console.sql).
    // The proposed flag marks a case that already has an open proposal.
    public static readonly IReadOnlyDictionary<string, string> Sql = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["review_payments"] = """
            select p.id, p.merchant_id, 'payment', p.amount_minor, p.currency, p.updated_at, null::text, null::uuid,
                   exists (select 1 from console.review_resolutions r
                            where r.state = 'proposed' and r.payment_id = p.id and r.attempt_id is null and r.refund_id is null)
              from payments p
             where p.state = 'REVIEW' and p.merchant_id = any(@merchants)
             order by p.updated_at
             limit @limit
            """,
        ["review_attempts"] = """
            select p.id, p.merchant_id, 'attempt:' || a.operation, p.amount_minor, p.currency, a.created_at, null::text, null::uuid,
                   exists (select 1 from console.review_resolutions r
                            where r.state = 'proposed' and r.payment_id = a.payment_id and r.attempt_id = a.attempt_id)
              from payment_attempts a
              join payments p on p.id = a.payment_id
             where a.state = 'REVIEW' and a.merchant_id = any(@merchants)
             order by a.created_at
             limit @limit
            """,
        ["review_refunds"] = """
            select p.id, p.merchant_id, 'refund', f.amount_minor, p.currency, f.created_at, null::text, null::uuid,
                   exists (select 1 from console.review_resolutions r
                            where r.state = 'proposed' and r.payment_id = f.payment_id and r.refund_id = f.id)
              from refunds f
              join payments p on p.id = f.payment_id
             where f.state = 'REVIEW' and f.merchant_id = any(@merchants)
             order by f.created_at
             limit @limit
            """,
        ["approval"] = """
            select p.id, p.merchant_id, r.kind, p.amount_minor, p.currency, r.created_at, r.outcome, r.proposed_by, false
              from console.review_resolutions r
              join payments p on p.id = r.payment_id
             where r.state = 'proposed' and p.merchant_id = any(@merchants)
             order by r.created_at
             limit @limit
            """,
        // Authorize and sale attempts show through their payment, so only capture and void here.
        ["unresolved_payments"] = """
            select p.id, p.merchant_id, 'payment', p.amount_minor, p.currency, p.updated_at, p.state, null::uuid, false
              from payments p
             where p.state in ('PSP_PENDING','UNKNOWN') and p.merchant_id = any(@merchants) and p.updated_at < @cutoff
             order by p.updated_at
             limit @limit
            """,
        ["unresolved_attempts"] = """
            select p.id, p.merchant_id, 'attempt:' || a.operation, p.amount_minor, p.currency, a.created_at, a.state, null::uuid, false
              from payment_attempts a
              join payments p on p.id = a.payment_id
             where a.state in ('PENDING','UNKNOWN') and a.operation in ('capture','void')
               and a.merchant_id = any(@merchants) and a.created_at < @cutoff
             order by a.created_at
             limit @limit
            """,
        ["unresolved_refunds"] = """
            select p.id, p.merchant_id, 'refund', f.amount_minor, p.currency, f.created_at, f.state, null::uuid, false
              from refunds f
              join payments p on p.id = f.payment_id
             where f.state in ('PENDING','UNKNOWN') and f.merchant_id = any(@merchants) and f.created_at < @cutoff
             order by f.created_at
             limit @limit
            """,
        ["expiring"] = """
            select p.id, p.merchant_id, 'payment', p.amount_minor, p.currency, p.authorized_until, null::text, null::uuid, false
              from payments p
             where p.state = 'AUTHORIZED' and p.merchant_id = any(@merchants) and p.authorized_until < @until
             order by p.authorized_until
             limit @limit
            """,
        // Every count stops at 1,001 rows, so a PSP outage can't make the page slow.
        ["counts"] = """
            select
              (select count(*) from (select 1 from payments where state = 'REVIEW' and merchant_id = any(@merchants) limit 1001) x)
              + (select count(*) from (select 1 from payment_attempts where state = 'REVIEW' and merchant_id = any(@merchants) limit 1001) x)
              + (select count(*) from (select 1 from refunds where state = 'REVIEW' and merchant_id = any(@merchants) limit 1001) x),
              (select count(*) from (select 1 from console.review_resolutions r join payments p on p.id = r.payment_id
                                      where r.state = 'proposed' and p.merchant_id = any(@merchants) limit 1001) x),
              (select count(*) from (select 1 from payments where state in ('PSP_PENDING','UNKNOWN') and merchant_id = any(@merchants) and updated_at < @cutoff limit 1001) x)
              + (select count(*) from (select 1 from payment_attempts where state in ('PENDING','UNKNOWN') and operation in ('capture','void') and merchant_id = any(@merchants) and created_at < @cutoff limit 1001) x)
              + (select count(*) from (select 1 from refunds where state in ('PENDING','UNKNOWN') and merchant_id = any(@merchants) and created_at < @cutoff limit 1001) x),
              (select count(*) from (select 1 from payments where state = 'AUTHORIZED' and merchant_id = any(@merchants) and authorized_until < @until limit 1001) x)
            """,
        // What one person can act on: review cases with no open proposal, someone else's
        // proposals, and authorizations ending soon.
        ["actionable"] = """
            select
              (select count(*) from (select 1 from payments p where p.state = 'REVIEW' and p.merchant_id = any(@merchants)
                 and not exists (select 1 from console.review_resolutions r where r.state = 'proposed' and r.payment_id = p.id and r.attempt_id is null and r.refund_id is null) limit 1001) x)
              + (select count(*) from (select 1 from payment_attempts a where a.state = 'REVIEW' and a.merchant_id = any(@merchants)
                 and not exists (select 1 from console.review_resolutions r where r.state = 'proposed' and r.payment_id = a.payment_id and r.attempt_id = a.attempt_id) limit 1001) x)
              + (select count(*) from (select 1 from refunds f where f.state = 'REVIEW' and f.merchant_id = any(@merchants)
                 and not exists (select 1 from console.review_resolutions r where r.state = 'proposed' and r.payment_id = f.payment_id and r.refund_id = f.id) limit 1001) x),
              (select count(*) from (select 1 from console.review_resolutions r join payments p on p.id = r.payment_id
                                      where r.state = 'proposed' and p.merchant_id = any(@merchants) and r.proposed_by <> @me limit 1001) x),
              (select count(*) from (select 1 from payments where state = 'AUTHORIZED' and merchant_id = any(@merchants) and authorized_until < @until limit 1001) x)
            """,
    };

    private static readonly (Queue Queue, string[] Reads)[] Reads =
    [
        (Queue.Review, ["review_payments", "review_attempts", "review_refunds"]),
        (Queue.Approval, ["approval"]),
        (Queue.Unresolved, ["unresolved_payments", "unresolved_attempts", "unresolved_refunds"]),
        (Queue.Expiring, ["expiring"]),
    ];

    public async Task<AttentionQueues> ReadAsync(DateTimeOffset now, CancellationToken ct)
    {
        var settings = options.Value;
        var lag = await readStore.ReplicaLagAsync(ct)
            ?? throw new ReplicaUnavailableException(new InvalidOperationException("The replica didn't answer."));

        // One round trip: every row read, then the counts.
        var rows = new Dictionary<Queue, List<AttentionRow>>();
        var counts = new long[4];
        try
        {
            await using var connection = await replica.OpenConnectionAsync(ct);
            await using var batch = connection.CreateBatch();
            foreach (var (_, reads) in Reads)
            {
                foreach (var read in reads)
                {
                    batch.BatchCommands.Add(Command(Sql[read], now, settings, me: null));
                }
            }

            batch.BatchCommands.Add(Command(Sql["counts"], now, settings, me: null));
            await using var reader = await batch.ExecuteReaderAsync(ct);
            foreach (var (queue, reads) in Reads)
            {
                var list = rows[queue] = [];
                foreach (var _ in reads)
                {
                    while (await reader.ReadAsync(ct))
                    {
                        list.Add(ReadRow(queue, reader));
                    }

                    await reader.NextResultAsync(ct);
                }
            }

            await reader.ReadAsync(ct);
            for (var i = 0; i < counts.Length; i++)
            {
                counts[i] = reader.GetInt64(i);
            }
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.QueryCanceled)
        {
            throw new ReadTimeoutException(exception);
        }
        catch (Exception exception) when (exception is NpgsqlException { IsTransient: true } or System.Net.Sockets.SocketException or TimeoutException)
        {
            throw new ReplicaUnavailableException(exception);
        }

        var orphans = await ReadOrphansAsync(ct);
        var emails = await EmailsAsync([.. rows[Queue.Approval].Select(r => r.Actor!.Value)], ct);
        var approval = rows[Queue.Approval].Select(r => r with { ActorEmail = emails.GetValueOrDefault(r.Actor!.Value) }).ToList();

        var limit = settings.RowsPerQueue;
        IReadOnlyList<AttentionRow> Top(IEnumerable<AttentionRow> source) => [.. source.OrderBy(r => r.At).Take(limit)];
        var views = new List<QueueView>
        {
            new(Queue.Review, Top(rows[Queue.Review]), (int)counts[0]),
            new(Queue.Approval, Top(approval), (int)counts[1]),
            new(Queue.Orphan, Top(orphans), orphans.Count),
            new(Queue.Unresolved, Top(rows[Queue.Unresolved]), (int)counts[2]),
            new(Queue.Expiring, Top(rows[Queue.Expiring]), (int)counts[3]),
        };
        return new AttentionQueues(views, lag);
    }

    public async Task<int> CountActionableAsync(Staff staff, DateTimeOffset now, CancellationToken ct)
    {
        var canAct = ConsolePolicies.Allows(staff.Role, ConsolePolicies.CanAct);
        if (!canAct)
        {
            return 0;
        }

        await using var connection = await replica.OpenConnectionAsync(ct);
        await using var batch = connection.CreateBatch();
        batch.BatchCommands.Add(Command(Sql["actionable"], now, options.Value, staff.Id));
        await using var reader = await batch.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        var review = reader.GetInt64(0);
        var approval = ConsolePolicies.Allows(staff.Role, ConsolePolicies.CanApprove) ? reader.GetInt64(1) : 0;
        var expiring = reader.GetInt64(2);
        return (int)(review + approval + expiring);
    }

    private NpgsqlBatchCommand Command(string sql, DateTimeOffset now, AttentionOptions settings, Guid? me)
    {
        var command = new NpgsqlBatchCommand(sql);
        command.Parameters.AddWithValue("merchants", search.Value.Merchants.ToArray());
        if (sql.Contains("@cutoff", StringComparison.Ordinal))
        {
            command.Parameters.AddWithValue("cutoff", now - settings.UnresolvedAfter);
        }

        if (sql.Contains("@until", StringComparison.Ordinal))
        {
            command.Parameters.AddWithValue("until", now + settings.ExpiringWithin);
        }

        if (sql.Contains("@limit", StringComparison.Ordinal))
        {
            command.Parameters.AddWithValue("limit", settings.RowsPerQueue);
        }

        if (me is { } id)
        {
            command.Parameters.AddWithValue("me", id);
        }

        return command;
    }

    private async Task<List<AttentionRow>> ReadOrphansAsync(CancellationToken ct)
    {
        var intents = await audit.FindOrphanIntentsAsync(auditOptions.Value.IntentLimit, ct);
        var emails = await EmailsAsync([.. intents.Select(i => i.StaffId)], ct);
        return [.. intents
            .Where(i => i.PaymentId is not null)
            .Select(i => new AttentionRow(
                Queue.Orphan, i.PaymentId!.Value, Guid.Empty, "action:" + i.Action, 0, string.Empty,
                i.CreatedAt, null, i.StaffId, emails.GetValueOrDefault(i.StaffId), false))];
    }

    // Staff and audit rows are written on the primary, so names are read there too.
    private async Task<Dictionary<Guid, string>> EmailsAsync(Guid[] ids, CancellationToken ct)
    {
        var result = new Dictionary<Guid, string>();
        if (ids.Length == 0)
        {
            return result;
        }

        await using var command = primary.CreateCommand("select id, email from console.staff where id = any(@ids)");
        command.Parameters.AddWithValue("ids", ids.Distinct().ToArray());
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result[reader.GetGuid(0)] = reader.GetString(1);
        }

        return result;
    }

    private static AttentionRow ReadRow(Queue queue, NpgsqlDataReader reader) => new(
        queue,
        reader.GetGuid(0),
        reader.GetGuid(1),
        reader.GetString(2),
        reader.GetInt64(3),
        reader.GetString(4).Trim(),
        reader.GetFieldValue<DateTimeOffset>(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetGuid(7),
        null,
        reader.GetBoolean(8));
}
