using Npgsql;
using Xunit;

namespace OpenPayments.Integration.Tests;

// Task C4: the console schema, its two roles, and what they must never be able to do.
[Collection("postgres")]
public sealed class ConsoleSchemaTests(DbFixture db)
{
    private static readonly string[] MoneyTables =
    [
        "payments", "payment_attempts", "refunds", "ledger_transactions", "ledger_entries",
        "accounts", "outbox", "idempotency_keys", "psp_events", "settlement_lines",
    ];

    private static readonly string[] ConsoleRoles = ["op_console_read", "op_console"];

    [Fact]
    public async Task Console_roles_cannot_write_money_tables()
    {
        await db.ResetAsync();
        await using var connection = await db.OpenAdminAsync();
        foreach (var role in ConsoleRoles)
        {
            foreach (var table in MoneyTables)
            {
                foreach (var privilege in new[] { "INSERT", "UPDATE", "DELETE", "TRUNCATE" })
                {
                    Assert.False(
                        await HasPrivilegeAsync(connection, role, "public." + table, privilege),
                        $"{role} has {privilege} on {table}");
                }
            }
        }
    }

    [Fact]
    public async Task Audit_log_cannot_be_updated_or_deleted_by_any_app_role()
    {
        await db.ResetAsync();
        await using var connection = await db.OpenAdminAsync();
        foreach (var role in new[] { "op_app", "op_console_read", "op_console" })
        {
            foreach (var privilege in new[] { "UPDATE", "DELETE", "TRUNCATE" })
            {
                Assert.False(
                    await HasPrivilegeAsync(connection, role, "console.audit_log", privilege),
                    $"{role} has {privilege} on console.audit_log");
            }
        }

        Assert.True(await HasPrivilegeAsync(connection, "op_console", "console.audit_log", "INSERT"));
        Assert.True(await HasPrivilegeAsync(connection, "op_app", "console.audit_log", "INSERT"));
    }

    [Fact]
    public async Task Self_approval_violates_check_constraint()
    {
        await db.ResetAsync();
        await using var connection = await db.OpenAdminAsync();
        var staff = Guid.NewGuid();
        var error = await Assert.ThrowsAsync<PostgresException>(() => InsertResolutionAsync(
            connection, Guid.NewGuid(), state: "applied", proposedBy: staff, approvedBy: staff));
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
    }

    [Fact]
    public async Task Second_open_proposal_for_a_case_violates_unique_index()
    {
        await db.ResetAsync();
        await using var connection = await db.OpenAdminAsync();
        var payment = Guid.NewGuid();
        await InsertResolutionAsync(connection, payment, "proposed", Guid.NewGuid(), null);

        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertResolutionAsync(connection, payment, "proposed", Guid.NewGuid(), null));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
        Assert.Equal("review_resolutions_one_open", error.ConstraintName);

        // A decided proposal doesn't block a new one.
        await InsertResolutionAsync(connection, payment, "rejected", Guid.NewGuid(), Guid.NewGuid());
    }

    [Fact]
    public async Task Op_app_money_grants_are_unchanged_by_this_migration()
    {
        await db.ResetAsync();
        await using var connection = await db.OpenAdminAsync();
        await using var command = new NpgsqlCommand(
            """
            select table_name || ':' || string_agg(privilege_type, ',' order by privilege_type)
            from information_schema.role_table_grants
            where grantee = 'op_app' and table_schema = 'public'
              and table_name = any(@tables)
            group by table_name
            order by table_name
            """,
            connection);
        command.Parameters.AddWithValue("tables", MoneyTables);
        var grants = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                grants.Add(reader.GetString(0));
            }
        }

        // Exactly what 0001_initial.sql grants, plus 0004's DELETE on psp_events (Task 19).
        Assert.Equal(
            [
                "accounts:SELECT",
                "idempotency_keys:DELETE,INSERT,SELECT,UPDATE",
                "ledger_entries:INSERT,SELECT",
                "ledger_transactions:INSERT,SELECT",
                "outbox:INSERT,SELECT,UPDATE",
                "payment_attempts:INSERT,SELECT,UPDATE",
                "payments:INSERT,SELECT,UPDATE",
                "psp_events:DELETE,INSERT,SELECT,UPDATE",
                "refunds:INSERT,SELECT,UPDATE",
                "settlement_lines:INSERT,SELECT,UPDATE",
            ],
            grants);
    }

    [Fact]
    public async Task Review_queue_indexes_exist_with_literal_conditions()
    {
        await db.ResetAsync();
        await using var connection = await db.OpenAdminAsync();
        foreach (var (index, table) in new[]
        {
            ("payments_review", "payments"),
            ("payment_attempts_review", "payment_attempts"),
            ("refunds_review", "refunds"),
        })
        {
            await using var command = new NpgsqlCommand(
                "select indexdef from pg_indexes where schemaname = 'public' and indexname = @index", connection);
            command.Parameters.AddWithValue("index", index);
            var definition = (string?)await command.ExecuteScalarAsync();
            Assert.NotNull(definition);
            // A partitioned table's index reads "ON ONLY public.<table>".
            Assert.Matches($@"ON (ONLY )?public\.{table} ", definition);
            Assert.Contains("WHERE (state = 'REVIEW'::text)", definition, StringComparison.Ordinal);
        }
    }

    private static async Task<bool> HasPrivilegeAsync(
        NpgsqlConnection connection, string role, string table, string privilege)
    {
        await using var command = new NpgsqlCommand("select has_table_privilege(@role, @table, @privilege)", connection);
        command.Parameters.AddWithValue("role", role);
        command.Parameters.AddWithValue("table", table);
        command.Parameters.AddWithValue("privilege", privilege);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static async Task InsertResolutionAsync(
        NpgsqlConnection connection, Guid payment, string state, Guid proposedBy, Guid? approvedBy)
    {
        await using var command = new NpgsqlCommand(
            """
            insert into console.review_resolutions
              (id, payment_id, kind, outcome, psp_answer, evidence, state, proposed_by, approved_by)
            values (gen_random_uuid(), @payment, 'payment', 'capture', 'captured', 'ticket 1', @state, @proposed, @approved)
            """,
            connection);
        command.Parameters.AddWithValue("payment", payment);
        command.Parameters.AddWithValue("state", state);
        command.Parameters.AddWithValue("proposed", proposedBy);
        command.Parameters.AddWithValue("approved", (object?)approvedBy ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }
}
