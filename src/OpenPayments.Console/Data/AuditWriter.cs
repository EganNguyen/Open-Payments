using Npgsql;

namespace OpenPayments.Console.Data;

public sealed record AuditIntent(
    Guid StaffId,
    string Action,
    Guid PaymentId,
    Guid? RefundId,
    long? AmountMinor,
    string? Currency,
    string Reason,
    string IdempotencyKey);

public sealed record AuditIntentRow(Guid StaffId, string Action, Guid? PaymentId, string IdempotencyKey, DateTimeOffset CreatedAt);

public interface IAuditWriter
{
    Task WriteIntentAsync(AuditIntent intent, CancellationToken ct);

    Task WriteOutcomeAsync(string idempotencyKey, Guid staffId, string result, CancellationToken ct);

    Task WriteEventAsync(Guid staffId, string action, string? detail, CancellationToken ct);

    Task<IReadOnlyList<AuditIntentRow>> FindOrphanIntentsAsync(TimeSpan olderThan, CancellationToken ct);

    /// <summary>Whether an action with this key has already started, so a resubmit goes
    /// straight to the API for its stored answer.</summary>
    Task<bool> IntentExistsAsync(string idempotencyKey, CancellationToken ct);
}

/// <summary>
/// A money action goes through the API, so its audit can't share the API's transaction. The
/// intent row commits before the call; if it can't be written, the action doesn't happen.
/// The outcome row follows the call. An intent with no outcome past the limit is an orphan,
/// checked by reading the payment. The table is append-only: nothing here updates or deletes.
/// </summary>
public sealed class AuditWriter(NpgsqlDataSource primary) : IAuditWriter
{
    public async Task WriteIntentAsync(AuditIntent intent, CancellationToken ct)
    {
        await using var command = primary.CreateCommand(
            """
            insert into console.audit_log (staff_id, phase, action, payment_id, refund_id, amount_minor, currency, reason, idempotency_key)
            values (@staff, 'intent', @action, @payment, @refund, @amount, @currency, @reason, @key)
            """);
        command.Parameters.AddWithValue("staff", intent.StaffId);
        command.Parameters.AddWithValue("action", intent.Action);
        command.Parameters.AddWithValue("payment", intent.PaymentId);
        command.Parameters.Add(new NpgsqlParameter<Guid?>("refund", intent.RefundId) { DataTypeName = "uuid" });
        command.Parameters.Add(new NpgsqlParameter<long?>("amount", intent.AmountMinor) { DataTypeName = "bigint" });
        command.Parameters.Add(new NpgsqlParameter<string?>("currency", intent.Currency) { DataTypeName = "text" });
        command.Parameters.AddWithValue("reason", intent.Reason);
        command.Parameters.AddWithValue("key", intent.IdempotencyKey);
        // Autocommit: the row is durable when this returns, before any API call.
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task WriteOutcomeAsync(string idempotencyKey, Guid staffId, string result, CancellationToken ct)
    {
        // Copy what the action was from its intent, so an outcome row reads on its own.
        await using var command = primary.CreateCommand(
            """
            insert into console.audit_log (staff_id, phase, action, payment_id, refund_id, amount_minor, currency, reason, idempotency_key, result)
            select @staff, 'outcome', coalesce(i.action, 'unknown'), i.payment_id, i.refund_id, i.amount_minor, i.currency, i.reason, @key, @result
            from (select 1) one
            left join lateral (
              select action, payment_id, refund_id, amount_minor, currency, reason from console.audit_log
              where idempotency_key = @key and phase = 'intent' order by id limit 1) i on true
            """);
        command.Parameters.AddWithValue("staff", staffId);
        command.Parameters.AddWithValue("key", idempotencyKey);
        command.Parameters.AddWithValue("result", result);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task WriteEventAsync(Guid staffId, string action, string? detail, CancellationToken ct)
    {
        await using var command = primary.CreateCommand(
            "insert into console.audit_log (staff_id, phase, action, result) values (@staff, 'event', @action, @detail)");
        command.Parameters.AddWithValue("staff", staffId);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.Add(new NpgsqlParameter<string?>("detail", detail) { DataTypeName = "text" });
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> IntentExistsAsync(string idempotencyKey, CancellationToken ct)
    {
        await using var command = primary.CreateCommand(
            "select exists (select 1 from console.audit_log where idempotency_key = @key and phase = 'intent')");
        command.Parameters.AddWithValue("key", idempotencyKey);
        return (bool)(await command.ExecuteScalarAsync(ct))!;
    }

    public async Task<IReadOnlyList<AuditIntentRow>> FindOrphanIntentsAsync(TimeSpan olderThan, CancellationToken ct)
    {
        // One row per key: a resubmit writes a second intent with the same key, and both are kept.
        await using var command = primary.CreateCommand(
            """
            select distinct on (i.idempotency_key) i.staff_id, i.action, i.payment_id, i.idempotency_key, i.created_at
            from console.audit_log i
            where i.phase = 'intent' and i.created_at < now() - @older
              and not exists (
                select 1 from console.audit_log o
                where o.phase = 'outcome' and o.idempotency_key = i.idempotency_key)
            order by i.idempotency_key, i.id
            """);
        command.Parameters.AddWithValue("older", olderThan);
        var rows = new List<AuditIntentRow>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new AuditIntentRow(
                reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetGuid(2),
                reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4)));
        }

        return [.. rows.OrderBy(row => row.CreatedAt)];
    }
}
