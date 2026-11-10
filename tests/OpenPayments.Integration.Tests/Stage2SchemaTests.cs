using Npgsql;
using Xunit;

namespace OpenPayments.Integration.Tests;

// Task 19: the Stage 2 schema. Events, recovery columns and the key links, with the
// partitioning, autovacuum and grant rules the design gate fixed.
[Collection("postgres")]
public sealed class Stage2SchemaTests(DbFixture db)
{
    [Fact]
    public async Task Psp_events_primary_key_dedupes_per_account()
    {
        await db.ResetAsync();
        await using var connection = await db.OpenAdminAsync();
        var account = Guid.NewGuid();
        await InsertEventAsync(connection, account, "evt_1");

        var error = await Assert.ThrowsAsync<PostgresException>(() => InsertEventAsync(connection, account, "evt_1"));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);

        // The same event ID under another account is a different event.
        await InsertEventAsync(connection, Guid.NewGuid(), "evt_1");
    }

    [Fact]
    public async Task Psp_events_is_partitioned_and_keeps_partition_key_in_primary_key()
    {
        await db.ResetAsync();
        await using var connection = await db.OpenAdminAsync();
        Assert.Equal("p", await ScalarAsync(connection, "select relkind::text from pg_class where oid = 'public.psp_events'::regclass"));
        Assert.Equal("h", await ScalarAsync(connection, "select partstrat::text from pg_partitioned_table where partrelid = 'public.psp_events'::regclass"));
        Assert.Equal(16L, await ScalarAsync(connection,
            "select count(*) from pg_inherits where inhparent = 'public.psp_events'::regclass"));

        var definition = (string)(await ScalarAsync(connection,
            "select pg_get_indexdef(indexrelid) from pg_index where indrelid = 'public.psp_events'::regclass and indisprimary"))!;
        Assert.Contains("psp_account_id", definition, StringComparison.Ordinal);
        Assert.Contains("psp_event_id", definition, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Psp_events_parked_and_payment_indexes_exist()
    {
        await db.ResetAsync();
        await using var connection = await db.OpenAdminAsync();
        await InsertEventAsync(connection, Guid.NewGuid(), "evt_parked", paymentId: Guid.NewGuid());

        foreach (var (index, predicate) in new[]
        {
            ("psp_events_parked", "processed_at IS NULL"),
            ("psp_events_payment", "payment_id IS NOT NULL"),
            ("psp_events_expirable", "processed_at IS NOT NULL"),
        })
        {
            var definition = (string?)await ScalarAsync(connection,
                "select indexdef from pg_indexes where schemaname = 'public' and indexname = @index", ("index", index));
            Assert.NotNull(definition);
            Assert.Contains(predicate, definition, StringComparison.Ordinal);
        }

        await using var transaction = await connection.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("set local enable_seqscan = off", connection, transaction))
        {
            await command.ExecuteNonQueryAsync();
        }

        foreach (var query in new[]
        {
            "explain (costs off) select 1 from psp_events where next_attempt_at <= now() and processed_at is null",
            $"explain (costs off) select 1 from psp_events where payment_id = '{Guid.NewGuid()}'",
        })
        {
            await using var command = new NpgsqlCommand(query, connection, transaction);
            var plan = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                plan.Add(reader.GetString(0));
            }

            Assert.Contains(plan, line => line.Contains("Index", StringComparison.Ordinal));
            Assert.DoesNotContain(plan, line => line.Contains("Seq Scan", StringComparison.Ordinal));
        }

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Attempts_and_refunds_carry_key_and_recovery_columns()
    {
        await db.ResetAsync();
        await using var connection = await db.OpenAdminAsync();
        foreach (var table in new[] { "payment_attempts", "refunds" })
        {
            var columns = new Dictionary<string, (string Nullable, string? Default)>();
            await using var command = new NpgsqlCommand(
                """
                select column_name, is_nullable, column_default
                from information_schema.columns
                where table_schema = 'public' and table_name = @table
                """,
                connection);
            command.Parameters.AddWithValue("table", table);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                columns[reader.GetString(0)] = (reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2));
            }

            Assert.True(columns.TryGetValue("idempotency_key", out var key), $"{table}.idempotency_key");
            Assert.Equal("YES", key.Nullable);
            Assert.True(columns.TryGetValue("resolves_key", out var resolves), $"{table}.resolves_key");
            Assert.Equal("NO", resolves.Nullable);
            Assert.Contains("true", resolves.Default, StringComparison.OrdinalIgnoreCase);
            Assert.True(columns.TryGetValue("next_recovery_at", out var next), $"{table}.next_recovery_at");
            Assert.Equal("YES", next.Nullable);
            Assert.True(columns.TryGetValue("recovery_attempts", out var attempts), $"{table}.recovery_attempts");
            Assert.Equal("NO", attempts.Nullable);
            Assert.Contains("0", attempts.Default, StringComparison.Ordinal);
            Assert.True(columns.ContainsKey("last_inquiry_at"), $"{table}.last_inquiry_at");
            Assert.True(columns.ContainsKey("last_inquiry_result"), $"{table}.last_inquiry_result");
        }
    }

    [Fact]
    public async Task Recovery_share_out_of_range_is_rejected()
    {
        await db.ResetAsync();
        await using var connection = await db.OpenAdminAsync();
        foreach (var value in new[] { "0", "1", "-0.1", "1.5" })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => InsertAccountAsync(connection, value));
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        }

        await InsertAccountAsync(connection, "0.10");
    }

    [Fact]
    public async Task Key_and_event_partitions_have_expiry_autovacuum_settings()
    {
        await db.ResetAsync();
        await using var connection = await db.OpenAdminAsync();
        foreach (var parent in new[] { "idempotency_keys", "psp_events" })
        {
            await using var command = new NpgsqlCommand(
                """
                select child.relname, child.reloptions
                from pg_inherits i
                join pg_class child on child.oid = i.inhrelid
                where i.inhparent = @parent::regclass
                order by child.relname
                """,
                connection);
            command.Parameters.AddWithValue("parent", "public." + parent);
            var rows = new List<(string Name, string[] Options)>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                rows.Add((reader.GetString(0), reader.IsDBNull(1) ? [] : reader.GetFieldValue<string[]>(1)));
            }

            Assert.Equal(16, rows.Count);
            foreach (var (name, options) in rows)
            {
                Assert.Contains($"autovacuum_vacuum_scale_factor=0.01", options, StringComparer.Ordinal);
                Assert.Contains($"autovacuum_vacuum_insert_scale_factor=0.01", options, StringComparer.Ordinal);
            }
        }
    }

    [Fact]
    public async Task Console_read_role_can_read_psp_events_but_not_write()
    {
        await db.ResetAsync();
        await using var connection = await db.OpenAdminAsync();
        Assert.True(await PrivilegeAsync(connection, "op_console_read", "psp_events", "SELECT"));
        foreach (var privilege in new[] { "INSERT", "UPDATE", "DELETE", "TRUNCATE" })
        {
            Assert.False(await PrivilegeAsync(connection, "op_console_read", "psp_events", privilege), privilege);
        }

        foreach (var privilege in new[] { "SELECT", "INSERT", "UPDATE", "DELETE" })
        {
            Assert.True(await PrivilegeAsync(connection, "op_app", "psp_events", privilege), privilege);
        }
    }

    [Fact]
    public async Task Op_app_money_grants_outside_psp_events_are_unchanged()
    {
        await db.ResetAsync();
        await using var connection = await db.OpenAdminAsync();
        string[] tables =
        [
            "accounts", "idempotency_keys", "ledger_entries", "ledger_transactions", "outbox",
            "payment_attempts", "payments", "refunds", "settlement_lines",
        ];
        await using var command = new NpgsqlCommand(
            """
            select table_name || ':' || string_agg(privilege_type, ',' order by privilege_type)
            from information_schema.role_table_grants
            where grantee = 'op_app' and table_schema = 'public' and table_name = any(@tables)
            group by table_name
            order by table_name
            """,
            connection);
        command.Parameters.AddWithValue("tables", tables);
        var grants = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            grants.Add(reader.GetString(0));
        }

        Assert.Equal(
            [
                "accounts:SELECT",
                "idempotency_keys:DELETE,INSERT,SELECT,UPDATE",
                "ledger_entries:INSERT,SELECT",
                "ledger_transactions:INSERT,SELECT",
                "outbox:INSERT,SELECT,UPDATE",
                "payment_attempts:INSERT,SELECT,UPDATE",
                "payments:INSERT,SELECT,UPDATE",
                "refunds:INSERT,SELECT,UPDATE",
                "settlement_lines:INSERT,SELECT,UPDATE",
            ],
            grants);
    }

    private static async Task InsertEventAsync(
        NpgsqlConnection connection, Guid account, string eventId, Guid? paymentId = null)
    {
        await using var command = new NpgsqlCommand(
            """
            insert into psp_events
              (psp_account_id, psp_event_id, source, payload, payment_id)
            values (@account, @event, 'callback', '{}'::jsonb, @payment)
            """,
            connection);
        command.Parameters.AddWithValue("account", account);
        command.Parameters.AddWithValue("event", eventId);
        command.Parameters.AddWithValue("payment", (object?)paymentId ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertAccountAsync(NpgsqlConnection connection, string recoveryShare)
    {
        await using var command = new NpgsqlCommand(
            """
            insert into psp_accounts
              (id, merchant_id, psp, mode, credential_ref, rate_limit_per_s, recovery_share)
            values (gen_random_uuid(), gen_random_uuid(), 'mock', 'test', 'test', 100, @share)
            """,
            connection);
        command.Parameters.AddWithValue("share", decimal.Parse(recoveryShare, System.Globalization.CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<bool> PrivilegeAsync(
        NpgsqlConnection connection, string role, string table, string privilege)
    {
        var result = await ScalarAsync(connection,
            "select has_table_privilege(@role, @table, @privilege)",
            ("role", role), ("table", "public." + table), ("privilege", privilege));
        return (bool)result;
    }

    private static async Task<object> ScalarAsync(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (await command.ExecuteScalarAsync())!;
    }
}
