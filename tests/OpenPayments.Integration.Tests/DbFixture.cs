using Npgsql;
using OpenPayments.Domain;
using Testcontainers.PostgreSql;
using Xunit;

namespace OpenPayments.Integration.Tests;

[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<DbFixture>;

public sealed class DbFixture : IAsyncLifetime
{
    private PostgreSqlContainer _container = null!;

    public string AdminConnectionString => _container.GetConnectionString();

    public string AppConnectionString
    {
        get
        {
            var builder = new NpgsqlConnectionStringBuilder(AdminConnectionString)
            {
                Username = "op_app",
                Password = "op_local_only",
                CommandTimeout = 0,
                Timeout = 2,
                MaxPoolSize = 10,
            };
            return builder.ConnectionString;
        }
    }

    public async Task InitializeAsync()
    {
        var repo = FindRepositoryRoot();
        var migrations = Path.Combine(repo, "db", "migrations");
        _container = new PostgreSqlBuilder("postgres:18.6")
            .WithDatabase("openpayments")
            .WithUsername("postgres")
            .WithPassword("op_local_only")
            .WithBindMount(migrations, "/schema/migrations")
            .Build();
        await _container.StartAsync();
        await ExecAdminAsync(
            "do $$ begin if not exists (select from pg_roles where rolname = 'op_app') then create role op_app login password 'op_local_only'; end if; end $$;");
        await ResetAsync();
    }

    public async Task ResetAsync()
    {
        await ExecAdminAsync(
            "drop schema if exists console cascade; drop schema if exists public cascade; create schema public; create table schema_migrations (version text primary key, checksum text not null, applied_at timestamptz not null default now()); grant usage on schema public to public;");
        foreach (var name in MigrationNames())
        {
            await ExecAdminAsync("-f", "/schema/migrations/" + name);
        }
    }

    public async Task<MerchantSeed> SeedMerchantAsync(
        string currency = "USD",
        string psp = "mock")
    {
        var merchant = new MerchantId(Guid.NewGuid());
        var pspAccountId = Guid.NewGuid();
        await using var connection = await OpenAdminAsync();
        await using (var command = new NpgsqlCommand(
            """
            insert into psp_accounts
              (id, merchant_id, psp, mode, credential_ref, rate_limit_per_s)
            values (@id, @merchant, @psp, 'live', 'test', 1000)
            """,
            connection))
        {
            command.Parameters.AddWithValue("id", pspAccountId);
            command.Parameters.AddWithValue("merchant", merchant.Value);
            command.Parameters.AddWithValue("psp", psp);
            await command.ExecuteNonQueryAsync();
        }

        foreach (var (type, moneyClass, kind) in new[]
        {
            ("auth_hold", "logical", "clearing"),
            ("auth_hold_contra", "logical", "clearing"),
            ("psp_receivable", "real", "terminal"),
            ("merchant_payable", "real", "terminal"),
            ("refund_reserved", "real", "clearing"),
            ("psp_refund_payable", "real", "terminal"),
        })
        {
            await using var command = new NpgsqlCommand(
                """
                insert into accounts
                  (id, merchant_id, account_type, sub_account, class, class_of_money, currency)
                values (@id, @merchant, @type, 0, @class, @money_class, @currency)
                """,
                connection);
            command.Parameters.AddWithValue("id", Guid.NewGuid());
            command.Parameters.AddWithValue("merchant", merchant.Value);
            command.Parameters.AddWithValue("type", type);
            command.Parameters.AddWithValue("class", kind);
            command.Parameters.AddWithValue("money_class", moneyClass);
            command.Parameters.AddWithValue("currency", currency);
            await command.ExecuteNonQueryAsync();
        }

        return new MerchantSeed(merchant, pspAccountId);
    }

    public async Task<SeededPayment> SeedPaymentAsync(
        MerchantSeed merchant,
        string state,
        long amountMinor = 1000,
        string operation = "authorize",
        string attemptState = "SUCCEEDED")
    {
        var paymentId = new PaymentId(Guid.NewGuid());
        var attemptId = new AttemptId(Guid.NewGuid());
        await using var connection = await OpenAdminAsync();
        await using (var command = new NpgsqlCommand(
            """
            insert into payments
              (id, merchant_id, state, amount_minor, currency, captured_minor,
               refunded_minor, refund_pending_minor, attempt_count, event_seq,
               expires_at, authorized_until)
            values
              (@id, @merchant, @state, @amount, 'USD', @captured, 0, 0, 1, 0,
               now() + interval '1 hour', now() + interval '1 hour')
            """,
            connection))
        {
            command.Parameters.AddWithValue("id", paymentId.Value);
            command.Parameters.AddWithValue("merchant", merchant.Merchant.Value);
            command.Parameters.AddWithValue("state", state);
            command.Parameters.AddWithValue("amount", amountMinor);
            command.Parameters.AddWithValue(
                "captured", state is "CAPTURED" or "PARTIALLY_REFUNDED" or "REFUNDED" ? amountMinor : 0);
            await command.ExecuteNonQueryAsync();
        }

        await using (var command = new NpgsqlCommand(
            """
            insert into payment_attempts
              (attempt_id, payment_id, merchant_id, operation, psp, psp_account_id,
               state, method_ref, method_type)
            values (@attempt, @payment, @merchant, @operation, 'mock', @psp_account,
                    @state, 'tok_test', 'card')
            """,
            connection))
        {
            command.Parameters.AddWithValue("attempt", attemptId.Value);
            command.Parameters.AddWithValue("payment", paymentId.Value);
            command.Parameters.AddWithValue("merchant", merchant.Merchant.Value);
            command.Parameters.AddWithValue("operation", operation);
            command.Parameters.AddWithValue("psp_account", merchant.PspAccountId);
            command.Parameters.AddWithValue("state", attemptState);
            await command.ExecuteNonQueryAsync();
        }

        return new SeededPayment(paymentId, attemptId);
    }

    public async Task<NpgsqlConnection> OpenAdminAsync()
    {
        var connection = new NpgsqlConnection(AdminConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    public async Task<NpgsqlConnection> OpenAppAsync()
    {
        var connection = new NpgsqlConnection(AppConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    public async Task<long> ScalarAsync(string sql)
    {
        await using var connection = await OpenAdminAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    public async Task ExecAdminAsync(params string[] arguments)
    {
        string[] command = arguments.Length == 2 && arguments[0] == "-f"
            ? ["psql", "-U", "postgres", "-d", "openpayments", "-v", "ON_ERROR_STOP=1", "-f", arguments[1]]
            : ["psql", "-U", "postgres", "-d", "openpayments", "-v", "ON_ERROR_STOP=1", "-c", arguments[0]];
        var result = await _container.ExecAsync(command);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Container command failed ({result.ExitCode}): {string.Join(' ', arguments)}\n{result.Stderr}");
        }
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    // Every migration, in the name order scripts/migrate.sh applies them.
    private static IEnumerable<string> MigrationNames() =>
        Directory.GetFiles(Path.Combine(FindRepositoryRoot(), "db", "migrations"), "*.sql")
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)!;

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "OpenPayments.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not find the repository root.");
    }
}

public sealed record MerchantSeed(MerchantId Merchant, Guid PspAccountId);

public sealed record SeededPayment(PaymentId PaymentId, AttemptId AttemptId);
