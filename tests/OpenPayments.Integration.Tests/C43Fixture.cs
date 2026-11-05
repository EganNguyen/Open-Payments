using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Npgsql;
using OpenPayments.Domain;
using Testcontainers.PostgreSql;
using Xunit;

namespace OpenPayments.Integration.Tests;

[CollectionDefinition("c43")]
public sealed class C43Collection : ICollectionFixture<C43Fixture>;

/// <summary>
/// A primary with synchronous_standby_names pointing at two standbys that never connect, so a
/// commit that has reached COMMIT waits in SyncRep. The same database is reachable directly and
/// through PgBouncer in transaction mode with query_timeout=0.
/// </summary>
public sealed class C43Fixture : IAsyncLifetime
{
    private const string Standbys = "ANY 1 (standby1)";
    private INetwork _network = null!;
    private PostgreSqlContainer _postgres = null!;
    private IContainer _pgbouncer = null!;
    private IContainer _standby = null!;

    public string AdminConnectionString => _postgres.GetConnectionString();

    public string PgBouncerConnectionString =>
        $"Host={_pgbouncer.Hostname};Port={_pgbouncer.GetMappedPublicPort(5432)};Database=openpayments;" +
        "Username=op_app;Password=op_local_only;Command Timeout=0;Timeout=2;MaxPoolSize=10;" +
        "No Reset On Close=true";

    public string ConnectionString(string mode, int maxPoolSize = 10) =>
        mode == "pgbouncer"
            ? new NpgsqlConnectionStringBuilder(PgBouncerConnectionString)
            {
                MaxPoolSize = maxPoolSize,
            }.ConnectionString
            : new NpgsqlConnectionStringBuilder(AdminConnectionString)
            {
                Username = "op_app",
                Password = "op_local_only",
                CommandTimeout = 0,
                Timeout = 2,
                MaxPoolSize = maxPoolSize,
            }.ConnectionString;

    public async Task InitializeAsync()
    {
        var repo = FindRepositoryRoot();
        var migration = Path.Combine(repo, "db", "migrations", "0001_initial.sql");
        _network = new NetworkBuilder().Build();
        await _network.CreateAsync();

        _postgres = new PostgreSqlBuilder("postgres:18.6")
            .WithDatabase("openpayments")
            .WithUsername("postgres")
            .WithPassword("op_local_only")
            .WithNetwork(_network)
            .WithNetworkAliases("postgres")
            .WithCommand(
                "-c", $"synchronous_standby_names={Standbys}",
                "-c", "synchronous_commit=on",
                "-c", "wal_level=replica",
                "-c", "max_wal_senders=10",
                "-c", "hot_standby=on")
            .WithBindMount(migration, "/schema/0001_initial.sql")
            .Build();
        await _postgres.StartAsync();

        _pgbouncer = new ContainerBuilder("edoburu/pgbouncer:v1.26.1-p0")
            .WithNetwork(_network)
            .WithNetworkAliases("pgbouncer")
            .WithEnvironment("DB_HOST", "postgres")
            .WithEnvironment("DB_PORT", "5432")
            .WithEnvironment("DB_USER", "op_app")
            .WithEnvironment("DB_PASSWORD", "op_local_only")
            .WithEnvironment("DB_NAME", "openpayments")
            .WithEnvironment("POOL_MODE", "transaction")
            .WithEnvironment("QUERY_TIMEOUT", "0")
            .WithEnvironment("AUTH_TYPE", "trust")
            .WithEnvironment("MAX_CLIENT_CONN", "100")
            .WithEnvironment("DEFAULT_POOL_SIZE", "20")
            .WithPortBinding(5432, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(5432))
            .Build();
        await _pgbouncer.StartAsync();

        _standby = new ContainerBuilder("postgres:18.6")
            .WithNetwork(_network)
            .WithEnvironment("PGPASSWORD", "op_local_only")
            .WithCommand(
                "sh", "-c",
                "mkdir -p /tmp/wal && chown postgres:postgres /tmp/wal && exec gosu postgres pg_receivewal -h postgres -U postgres -D /tmp/wal --application-name=standby1 --no-sync --verbose")
            .Build();
        await _standby.StartAsync();

        await ExecAdminAsync(
            "do $$ begin if not exists (select from pg_roles where rolname = 'op_app') then create role op_app login password 'op_local_only'; end if; end $$;");
        await ResetAsync();
    }

    public async Task ResetAsync()
    {
        await UnblockAsync(terminateBackends: true);
        await ExecAdminAsync(
            "drop schema if exists public cascade; create schema public; create table schema_migrations (version text primary key, checksum text not null, applied_at timestamptz not null default now()); grant usage on schema public to public;");
        await ExecAdminAsync("-f", "/schema/0001_initial.sql");
        await WaitForStandbyAsync();
    }

    /// <summary>Stop the standby's WAL receiver, so COMMIT waits in SyncRep.</summary>
    public async Task BlockAsync()
    {
        await _standby.ExecAsync(["kill", "-STOP", "1"]);
        await Task.Delay(200);
    }

    /// <summary>Let the standby receive WAL again, which wakes every waiting COMMIT.</summary>
    public async Task UnblockAsync(bool terminateBackends)
    {
        await _standby.ExecAsync(["kill", "-CONT", "1"]);
        await WaitForStandbyAsync();
        if (terminateBackends)
        {
            await TerminateAppBackendsAsync();
        }
        else
        {
            await WaitForNoSyncRepWaitersAsync();
        }
    }

    public async Task<MerchantSeed> SeedMerchantAsync()
    {
        var merchant = new MerchantId(Guid.NewGuid());
        var pspAccountId = Guid.NewGuid();
        await using var connection = await OpenAdminAsync();
        await using (var command = new NpgsqlCommand(
            """
            insert into psp_accounts (id, merchant_id, psp, mode, credential_ref, rate_limit_per_s)
            values (@id, @merchant, 'mock', 'live', 'test', 1000)
            """,
            connection))
        {
            command.Parameters.AddWithValue("id", pspAccountId);
            command.Parameters.AddWithValue("merchant", merchant.Value);
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
                values (@id, @merchant, @type, 0, @class, @money_class, 'USD')
                """,
                connection);
            command.Parameters.AddWithValue("id", Guid.NewGuid());
            command.Parameters.AddWithValue("merchant", merchant.Value);
            command.Parameters.AddWithValue("type", type);
            command.Parameters.AddWithValue("class", kind);
            command.Parameters.AddWithValue("money_class", moneyClass);
            await command.ExecuteNonQueryAsync();
        }

        return new MerchantSeed(merchant, pspAccountId);
    }

    public async Task<int> CountSyncRepWaitersAsync()
    {
        await using var connection = await OpenAdminAsync();
        await using var command = new NpgsqlCommand(
            "select count(*)::int from pg_stat_activity where usename = 'op_app' and wait_event_type = 'SyncRep'",
            connection);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    public async Task<int> CountAppAttemptsAsync()
    {
        await using var connection = await OpenAdminAsync();
        await using var command = new NpgsqlCommand(
            "select count(*)::int from payment_attempts",
            connection);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    public async Task TerminateBackendAsync(int pid)
    {
        await using var connection = await OpenAdminAsync();
        await using var command = new NpgsqlCommand("select pg_terminate_backend(@pid)", connection);
        command.Parameters.AddWithValue("pid", pid);
        await command.ExecuteScalarAsync();
    }

    public async Task CancelBackendAsync(int pid)
    {
        await using var connection = await OpenAdminAsync();
        await using var command = new NpgsqlCommand("select pg_cancel_backend(@pid)", connection);
        command.Parameters.AddWithValue("pid", pid);
        await command.ExecuteScalarAsync();
    }

    public async Task<NpgsqlConnection> OpenAdminAsync()
    {
        var connection = new NpgsqlConnection(AdminConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    public async Task DisposeAsync()
    {
        try
        {
            await UnblockAsync(terminateBackends: true);
        }
        catch
        {
            // Best effort: the container may already be stopping.
        }

        await _standby.DisposeAsync();
        await _pgbouncer.DisposeAsync();
        await _postgres.DisposeAsync();
        await _network.DeleteAsync();
    }

    private async Task WaitForStandbyAsync()
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await using var connection = await OpenAdminAsync();
            await using var command = new NpgsqlCommand(
                "select count(*)::int from pg_stat_replication where application_name = 'standby1' and state = 'streaming'",
                connection);
            if ((int)(await command.ExecuteScalarAsync())! >= 1)
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new InvalidOperationException("standby1 did not start streaming.");
    }

    private async Task WaitForNoSyncRepWaitersAsync()
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (await CountSyncRepWaitersAsync() == 0)
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new InvalidOperationException("SyncRep waiters did not clear.");
    }

    private async Task TerminateAppBackendsAsync()
    {
        await using var connection = await OpenAdminAsync();
        await using var command = new NpgsqlCommand(
            "select pg_terminate_backend(pid) from pg_stat_activity where usename = 'op_app' and pid <> pg_backend_pid()",
            connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task ExecAdminAsync(params string[] arguments)
    {
        string[] command = arguments.Length == 2 && arguments[0] == "-f"
            ? ["psql", "-U", "postgres", "-d", "openpayments", "-v", "ON_ERROR_STOP=1", "-f", arguments[1]]
            : ["psql", "-U", "postgres", "-d", "openpayments", "-v", "ON_ERROR_STOP=1", "-c", arguments[0]];
        var result = await _postgres.ExecAsync(command);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Container command failed ({result.ExitCode}): {string.Join(' ', arguments)}\n{result.Stderr}");
        }
    }

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
