using DotNet.Testcontainers.Builders;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace OpenPayments.Checks.Tests;

public sealed class DbFixture : IAsyncLifetime
{
    private PostgreSqlContainer _container = null!;

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        var repo = FindRepositoryRoot();
        var migration = Path.Combine(repo, "db", "migrations", "0001_initial.sql");
        _container = new PostgreSqlBuilder("postgres:18.6")
            .WithDatabase("openpayments")
            .WithUsername("postgres")
            .WithPassword("op_local_only")
            .WithBindMount(migration, "/schema/0001_initial.sql")
            .Build();
        await _container.StartAsync();
        await ExecAsync(
            "psql", "-U", "postgres", "-d", "openpayments", "-v", "ON_ERROR_STOP=1", "-c",
            "do $$ begin if not exists (select from pg_roles where rolname = 'op_app') then create role op_app login password 'op_local_only'; end if; end $$;");
    }

    public async Task ResetAsync()
    {
        await ExecAsync(
            "psql", "-U", "postgres", "-d", "openpayments", "-v", "ON_ERROR_STOP=1", "-c",
            "drop schema if exists public cascade; create schema public; drop schema if exists ck cascade; create table schema_migrations (version text primary key, checksum text not null, applied_at timestamptz not null default now());");
        await ExecAsync(
            "psql", "-U", "postgres", "-d", "openpayments", "-v", "ON_ERROR_STOP=1", "-f",
            "/schema/0001_initial.sql");
    }

    public async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    private async Task ExecAsync(params string[] arguments)
    {
        var result = await _container.ExecAsync(arguments);
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
