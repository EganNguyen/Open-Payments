using Npgsql;
using OpenPayments.Domain;
using Testcontainers.PostgreSql;
using Xunit;

namespace OpenPayments.Reconciler.Tests;

[CollectionDefinition("reconciler-postgres")]
public sealed class ReconcilerCollection : ICollectionFixture<ReconcilerFixture>;

public sealed class ReconcilerFixture : IAsyncLifetime
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
            };
            return builder.ConnectionString;
        }
    }

    public async Task InitializeAsync()
    {
        var repo = FindRoot();
        _container = new PostgreSqlBuilder("postgres:18.6")
            .WithDatabase("openpayments")
            .WithUsername("postgres")
            .WithPassword("op_local_only")
            .WithBindMount(Path.Combine(repo, "db", "migrations"), "/schema/migrations")
            .Build();
        await _container.StartAsync();
        await ExecAdminAsync(
            "do $$ begin if not exists (select from pg_roles where rolname = 'op_app') then create role op_app login password 'op_local_only'; end if; end $$; alter role op_app set lock_timeout = '200ms';");
        await ResetAsync();
    }

    public async Task ResetAsync()
    {
        await ExecAdminAsync(
            "drop schema if exists console cascade; drop schema if exists public cascade; create schema public; create table schema_migrations (version text primary key, checksum text not null, applied_at timestamptz not null default now()); grant usage on schema public to public;");
        foreach (var file in Directory.GetFiles(Path.Combine(FindRoot(), "db", "migrations"), "*.sql").Order())
        {
            await ExecAdminAsync("-f", "/schema/migrations/" + Path.GetFileName(file));
        }
    }

    public async Task<(Guid Merchant, Guid Account, Guid Payment, Guid Attempt)> SeedPendingAsync()
    {
        var merchant = Guid.NewGuid();
        var account = Guid.NewGuid();
        var payment = Guid.NewGuid();
        var attempt = Guid.NewGuid();
        await ExecAdminAsync($"""
            insert into psp_accounts (id, merchant_id, psp, mode, credential_ref, rate_limit_per_s)
            values ('{account}', '{merchant}', 'mock', 'test', 'ref', 1000);
            insert into accounts (id, merchant_id, account_type, class, class_of_money, currency) values
              (gen_random_uuid(), '{merchant}', 'auth_hold', 'clearing', 'logical', 'USD'),
              (gen_random_uuid(), '{merchant}', 'auth_hold_contra', 'clearing', 'logical', 'USD'),
              (gen_random_uuid(), '{merchant}', 'psp_receivable', 'terminal', 'real', 'USD'),
              (gen_random_uuid(), '{merchant}', 'merchant_payable', 'terminal', 'real', 'USD'),
              (gen_random_uuid(), '{merchant}', 'refund_reserved', 'clearing', 'real', 'USD'),
              (gen_random_uuid(), '{merchant}', 'psp_refund_payable', 'terminal', 'real', 'USD');
            insert into payments (id, merchant_id, state, amount_minor, currency, expires_at)
            values ('{payment}', '{merchant}', 'PSP_PENDING', 1000, 'USD', now() + interval '1 day');
            insert into payment_attempts
              (attempt_id, payment_id, merchant_id, operation, psp, psp_account_id, state, method_ref, method_type, idempotency_key)
            values ('{attempt}', '{payment}', '{merchant}', 'authorize', 'mock', '{account}', 'PENDING', 'tok', 'card', 'k1');
            """);
        return (merchant, account, payment, attempt);
    }

    public async Task<(Guid Payment, Guid Refund)> SeedUnknownRefundAsync(Guid merchant, Guid account)
    {
        var payment = Guid.NewGuid();
        var refund = Guid.NewGuid();
        await ExecAdminAsync(
            "insert into payments (id, merchant_id, state, amount_minor, currency, captured_minor, expires_at) values ('"
            + payment + "', '" + merchant + "', 'CAPTURED', 1000, 'USD', 1000, now() + interval '1 day'); "
            + "insert into refunds (id, payment_id, merchant_id, amount_minor, state, psp, psp_account_id, created_at) values ('"
            + refund + "', '" + payment + "', '" + merchant + "', 1000, 'UNKNOWN', 'mock', '" + account
            + "', now() - interval '1 minute');");
        return (payment, refund);
    }

    public async Task InsertCallbackEventAsync(
        Guid account, Guid attempt, string eventId, DateTimeOffset receivedAt)
    {
        var payload =
            $$"""{"event_id":"{{eventId}}","type":"authorize.approved","reference":"{{attempt}}","psp_reference":"psp_1","amount_minor":1000,"currency":"USD"}""";
        await ExecAdminAsync($"""
            insert into psp_events
              (psp_account_id, psp_event_id, source, payload, received_at, next_attempt_at)
            values ('{account}', '{eventId}', 'callback', '{payload}'::jsonb, '{receivedAt:O}', now() - interval '1 second');
            """);
    }

    public async Task<long> CountAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    public async Task<string?> TextAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (string?)await command.ExecuteScalarAsync();
    }

    public async Task ExecAdminAsync(params string[] arguments)
    {
        var command = arguments.Length == 2 && arguments[0] == "-f"
            ? new[] { "psql", "-U", "postgres", "-d", "openpayments", "-v", "ON_ERROR_STOP=1", "-f", arguments[1] }
            : new[] { "psql", "-U", "postgres", "-d", "openpayments", "-v", "ON_ERROR_STOP=1", "-c", arguments[0] };
        var result = await _container.ExecAsync(command);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(result.Stderr);
        }
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OpenPayments.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
