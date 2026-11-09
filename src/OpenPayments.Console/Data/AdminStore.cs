using Npgsql;
using OpenPayments.Console.Security;

namespace OpenPayments.Console.Data;

public sealed record AuditRow(
    long Id, DateTimeOffset At, Guid StaffId, string? Email, string Phase, string Action,
    Guid? PaymentId, long? AmountMinor, string? Currency, string? Reason, string? Key, string? Result, bool Orphan);

public sealed record AuditQuery(DateTimeOffset From, DateTimeOffset To, Guid? StaffId, Guid? PaymentId, long? BeforeId, int Limit = 50);

public sealed record AuditPage(IReadOnlyList<AuditRow> Rows, long? NextBeforeId);

public enum StaffChange
{
    Done,
    LastAdmin,
    NotFound,
}

public interface IAdminStore
{
    Task<AuditPage> ReadAuditAsync(AuditQuery query, TimeSpan orphanAfter, CancellationToken ct);

    Task<IReadOnlyList<Staff>> ListStaffAsync(CancellationToken ct);

    Task<StaffChange> SetRoleAsync(Guid staffId, ConsoleRole role, Guid byStaffId, CancellationToken ct);

    Task<StaffChange> SetDisabledAsync(Guid staffId, bool disabled, Guid byStaffId, CancellationToken ct);

    Task AddStaffAsync(string subject, string email, ConsoleRole role, Guid byStaffId, CancellationToken ct);
}

/// <summary>Audit reads and staff management, as op_console on the primary. Every staff change
/// writes its audit event in the same transaction, and the last active admin can't be removed.</summary>
public sealed class AdminStore(NpgsqlDataSource primary) : IAdminStore
{
    public async Task<AuditPage> ReadAuditAsync(AuditQuery query, TimeSpan orphanAfter, CancellationToken ct)
    {
        await using var command = primary.CreateCommand(
            """
            select a.id, a.created_at, a.staff_id, s.email, a.phase, a.action, a.payment_id, a.amount_minor, a.currency,
                   a.reason, a.idempotency_key, a.result,
                   a.phase = 'intent' and a.created_at < now() - @orphan and not exists (
                     select 1 from console.audit_log o where o.phase = 'outcome' and o.idempotency_key = a.idempotency_key)
            from console.audit_log a left join console.staff s on s.id = a.staff_id
            where a.created_at >= @from and a.created_at < @to
              and (@staff::uuid is null or a.staff_id = @staff)
              and (@payment::uuid is null or a.payment_id = @payment)
              and (@before::bigint is null or a.id < @before)
            order by a.id desc
            limit @limit
            """);
        command.Parameters.AddWithValue("orphan", orphanAfter);
        command.Parameters.AddWithValue("from", query.From);
        command.Parameters.AddWithValue("to", query.To);
        command.Parameters.Add(new NpgsqlParameter<Guid?>("staff", query.StaffId) { DataTypeName = "uuid" });
        command.Parameters.Add(new NpgsqlParameter<Guid?>("payment", query.PaymentId) { DataTypeName = "uuid" });
        command.Parameters.Add(new NpgsqlParameter<long?>("before", query.BeforeId) { DataTypeName = "bigint" });
        command.Parameters.AddWithValue("limit", query.Limit + 1);
        var rows = new List<AuditRow>();
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                rows.Add(new AuditRow(
                    reader.GetInt64(0), reader.GetFieldValue<DateTimeOffset>(1), reader.GetGuid(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetGuid(6), reader.IsDBNull(7) ? null : reader.GetInt64(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9),
                    reader.IsDBNull(10) ? null : reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetString(11),
                    reader.GetBoolean(12)));
            }
        }

        long? next = null;
        if (rows.Count > query.Limit)
        {
            rows.RemoveAt(rows.Count - 1);
            next = rows[^1].Id;
        }

        return new AuditPage(rows, next);
    }

    public async Task<IReadOnlyList<Staff>> ListStaffAsync(CancellationToken ct)
    {
        await using var command = primary.CreateCommand(
            "select id, oidc_subject, email, role, disabled_at from console.staff order by disabled_at nulls first, email");
        var rows = new List<Staff>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (ConsolePolicies.TryParse(reader.GetString(3), out var role))
            {
                rows.Add(new Staff(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), role,
                    reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4)));
            }
        }

        return rows;
    }

    public Task<StaffChange> SetRoleAsync(Guid staffId, ConsoleRole role, Guid byStaffId, CancellationToken ct) =>
        ChangeAsync(staffId, byStaffId, "update console.staff set role = @value where id = @id", ConsolePolicies.ToText(role),
            removesAdmin: role != ConsoleRole.Admin, "staff.role", "role " + ConsolePolicies.ToText(role), ct);

    public Task<StaffChange> SetDisabledAsync(Guid staffId, bool disabled, Guid byStaffId, CancellationToken ct) =>
        ChangeAsync(staffId, byStaffId,
            disabled ? "update console.staff set disabled_at = now() where id = @id and @value = @value"
                     : "update console.staff set disabled_at = null where id = @id and @value = @value",
            "x", removesAdmin: disabled, disabled ? "staff.disable" : "staff.enable", null, ct);

    public async Task AddStaffAsync(string subject, string email, ConsoleRole role, Guid byStaffId, CancellationToken ct)
    {
        await using var connection = await primary.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var id = Guid.CreateVersion7();
        await using (var insert = new NpgsqlCommand(
            "insert into console.staff (id, oidc_subject, email, role) values (@id, @subject, @email, @role)", connection, transaction))
        {
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("subject", subject);
            insert.Parameters.AddWithValue("email", email);
            insert.Parameters.AddWithValue("role", ConsolePolicies.ToText(role));
            await insert.ExecuteNonQueryAsync(ct);
        }

        await AuditAsync(connection, transaction, byStaffId, "staff.add", $"{email} as {ConsolePolicies.ToText(role)}", ct);
        await transaction.CommitAsync(ct);
    }

    private static async Task<StaffChange> ChangeAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid staffId, string sql, string value, bool removesAdmin, CancellationToken ct)
    {
        // Lock every active admin, so two changes can't each leave the other as the last one.
        var admins = new List<Guid>();
        await using (var lockAdmins = new NpgsqlCommand(
            "select id from console.staff where role = 'admin' and disabled_at is null order by id for update", connection, transaction))
        await using (var reader = await lockAdmins.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                admins.Add(reader.GetGuid(0));
            }
        }

        if (removesAdmin && admins.Count == 1 && admins[0] == staffId)
        {
            return StaffChange.LastAdmin;
        }

        await using var update = new NpgsqlCommand(sql, connection, transaction);
        update.Parameters.AddWithValue("id", staffId);
        update.Parameters.AddWithValue("value", value);
        return await update.ExecuteNonQueryAsync(ct) == 1 ? StaffChange.Done : StaffChange.NotFound;
    }

    private async Task<StaffChange> ChangeAsync(
        Guid staffId, Guid byStaffId, string sql, string value, bool removesAdmin, string action, string? detail, CancellationToken ct)
    {
        await using var connection = await primary.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var result = await ChangeAsync(connection, transaction, staffId, sql, value, removesAdmin, ct);
        if (result != StaffChange.Done)
        {
            await transaction.RollbackAsync(ct);
            return result;
        }

        await AuditAsync(connection, transaction, byStaffId, action, $"staff {staffId:D}" + (detail is null ? string.Empty : ": " + detail), ct);
        await transaction.CommitAsync(ct);
        return result;
    }

    private static async Task AuditAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid byStaffId, string action, string detail, CancellationToken ct)
    {
        await using var audit = new NpgsqlCommand(
            "insert into console.audit_log (staff_id, phase, action, result) values (@staff, 'event', @action, @detail)", connection, transaction);
        audit.Parameters.AddWithValue("staff", byStaffId);
        audit.Parameters.AddWithValue("action", action);
        audit.Parameters.AddWithValue("detail", detail);
        await audit.ExecuteNonQueryAsync(ct);
    }
}
