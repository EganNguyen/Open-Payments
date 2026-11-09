using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace OpenPayments.Console.Tests;

[CollectionDefinition("console-db")]
public sealed class ConsoleDbCollection : ICollectionFixture<ConsoleDb>;

/// <summary>
/// One PostgreSQL 18.6 with two databases built from every migration: "primary" and "replica".
/// A test can put a payment on the primary only, standing in for replica lag. The console
/// connects as login users that hold op_console_read and op_console, as a deployment would.
/// </summary>
public sealed class ConsoleDb : IAsyncLifetime
{
    public const string ReadUser = "console_read_test";
    public const string WriteUser = "console_write_test";
    public const string Password = "op_local_only";
    public static readonly Guid Merchant = Guid.Parse("00000000-0000-7000-8000-0000000c0001");
    public static readonly Guid OtherMerchant = Guid.Parse("00000000-0000-7000-8000-0000000c0002");

    private PostgreSqlContainer _container = null!;

    public string AdminFor(string database) =>
        new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = database }.ConnectionString;

    public string As(string user, string database) =>
        new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = database,
            Username = user,
            Password = Password,
        }.ConnectionString;

    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder("postgres:18.6")
            .WithDatabase("postgres")
            .WithUsername("postgres")
            .WithPassword(Password)
            .WithBindMount(Path.Combine(FindRepositoryRoot(), "db", "migrations"), "/schema/migrations")
            .Build();
        await _container.StartAsync();
        await ExecAsync("postgres", "create role op_app login password 'op_local_only'");
        foreach (var database in new[] { "primary", "replica" })
        {
            await ExecAsync("postgres", $"create database \"{database}\"");
            await ExecAsync(database, "create table schema_migrations (version text primary key, checksum text not null, applied_at timestamptz not null default now())");
            foreach (var file in Directory.GetFiles(Path.Combine(FindRepositoryRoot(), "db", "migrations"), "*.sql").Order(StringComparer.Ordinal))
            {
                var result = await _container.ExecAsync(
                    ["psql", "-U", "postgres", "-d", database, "-v", "ON_ERROR_STOP=1", "-q", "-f", "/schema/migrations/" + Path.GetFileName(file)]);
                if (result.ExitCode != 0)
                {
                    throw new InvalidOperationException(result.Stderr);
                }
            }
        }

        await ExecAsync("postgres", $"create role {ReadUser} login password '{Password}' in role op_console_read");
        await ExecAsync("postgres", $"create role {WriteUser} login password '{Password}' in role op_console");
    }

    public async Task ResetAsync()
    {
        foreach (var database in new[] { "primary", "replica" })
        {
            await ExecAsync(database, "truncate payments, payment_attempts, refunds, ledger_transactions, ledger_entries, console.staff, console.audit_log, console.review_resolutions cascade");
        }
    }

    public async Task<Guid> SeedPaymentAsync(
        string database,
        string state = "CAPTURED",
        long amountMinor = 1050,
        string currency = "USD",
        Guid? merchant = null,
        DateTimeOffset? createdAt = null,
        Guid? id = null,
        long captured = -1)
    {
        var paymentId = id ?? Guid.CreateVersion7();
        await using var connection = new NpgsqlConnection(AdminFor(database));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            insert into payments (id, merchant_id, state, amount_minor, currency, captured_minor, expires_at, created_at, updated_at)
            values (@id, @merchant, @state, @amount, @currency, @captured, now() + interval '1 day', @created, @created)
            """,
            connection);
        command.Parameters.AddWithValue("id", paymentId);
        command.Parameters.AddWithValue("merchant", merchant ?? Merchant);
        command.Parameters.AddWithValue("state", state);
        command.Parameters.AddWithValue("amount", amountMinor);
        command.Parameters.AddWithValue("currency", currency);
        command.Parameters.AddWithValue("captured", captured >= 0 ? captured : state is "CAPTURED" or "PARTIALLY_REFUNDED" or "REFUNDED" ? amountMinor : 0);
        command.Parameters.AddWithValue("created", createdAt ?? DateTimeOffset.UtcNow);
        await command.ExecuteNonQueryAsync();
        return paymentId;
    }

    public static readonly Guid PspAccount = Guid.Parse("00000000-0000-7000-8000-0000000c0003");

    public async Task SeedAttemptAsync(
        string database, Guid payment, string operation, string state, DateTimeOffset at,
        string? failureCode = null, string? pspReference = null)
    {
        await ExecAsync(database, $"""
            insert into psp_accounts (id, merchant_id, psp, mode, credential_ref, rate_limit_per_s)
            values ('{PspAccount}', '{Merchant}', 'mock', 'test', 'ref', 100) on conflict do nothing
            """);
        await using var connection = new NpgsqlConnection(AdminFor(database));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            insert into payment_attempts
              (attempt_id, payment_id, merchant_id, operation, psp, psp_account_id, state, failure_code,
               method_ref, method_type, psp_reference, created_at)
            values (@id, @payment, @merchant, @operation, 'mock', @psp, @state, @failure,
                    case when @operation in ('authorize','sale') then 'tok' end,
                    case when @operation in ('authorize','sale') then 'card' end, @reference, @at)
            """,
            connection);
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("payment", payment);
        command.Parameters.AddWithValue("merchant", Merchant);
        command.Parameters.AddWithValue("operation", operation);
        command.Parameters.AddWithValue("psp", PspAccount);
        command.Parameters.AddWithValue("state", state);
        command.Parameters.AddWithValue("failure", (object?)failureCode ?? DBNull.Value);
        command.Parameters.AddWithValue("reference", (object?)pspReference ?? DBNull.Value);
        command.Parameters.AddWithValue("at", at);
        await command.ExecuteNonQueryAsync();
    }

    public async Task SeedRefundAsync(string database, Guid payment, long amountMinor, string state, DateTimeOffset at)
    {
        await ExecAsync(database, $"""
            insert into psp_accounts (id, merchant_id, psp, mode, credential_ref, rate_limit_per_s)
            values ('{PspAccount}', '{Merchant}', 'mock', 'test', 'ref', 100) on conflict do nothing
            """);
        var pending = state is "PENDING" or "UNKNOWN" ? amountMinor : 0;
        var done = state == "SUCCEEDED" ? amountMinor : 0;
        await ExecAsync(database, $"""
            update payments set refund_pending_minor = refund_pending_minor + {pending},
                                refunded_minor = refunded_minor + {done} where id = '{payment}';
            insert into refunds (id, payment_id, merchant_id, amount_minor, state, psp, psp_account_id, created_at)
            values ('{Guid.CreateVersion7()}', '{payment}', '{Merchant}', {amountMinor}, '{state}', 'mock', '{PspAccount}', '{at:O}');
            """);
    }

    public async Task ExecAsync(string database, string sql)
    {
        await using var connection = new NpgsqlConnection(AdminFor(database));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OpenPayments.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}

/// <summary>The console wired to <see cref="ConsoleDb"/>: replica reads from "replica", primary
/// reads and writes from "primary".</summary>
public sealed class DbConsoleFactory : ConsoleFactory
{
    public DbConsoleFactory(ConsoleDb db, Action<Dictionary<string, string?>>? configure = null)
    {
        Settings["Console:Db:Primary"] = db.As(ConsoleDb.WriteUser, "primary");
        Settings["Console:Db:PrimaryRead"] = db.As(ConsoleDb.ReadUser, "primary");
        Settings["Console:Db:Replica"] = db.As(ConsoleDb.ReadUser, "replica");
        Settings["Console:Merchants:0"] = ConsoleDb.Merchant.ToString();
        Settings["Console:Api:Keys:" + ConsoleDb.Merchant.ToString("D")] = "test-service-key";
        configure?.Invoke(Settings);
    }
}
