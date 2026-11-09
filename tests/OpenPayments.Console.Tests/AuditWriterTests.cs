using Npgsql;
using OpenPayments.Console.Data;
using Xunit;

namespace OpenPayments.Console.Tests;

// Task C10: the audit writer, as op_console on the primary.
[Collection("console-db")]
public sealed class AuditWriterTests(ConsoleDb db)
{
    [Fact]
    public async Task Intent_commits_before_the_call_returns()
    {
        await db.ResetAsync();
        await using var source = NpgsqlDataSource.Create(db.As(ConsoleDb.WriteUser, "primary"));
        var writer = new AuditWriter(source);
        var key = "key-" + Guid.NewGuid();

        await writer.WriteIntentAsync(Intent(key), CancellationToken.None);

        // Another connection sees it at once, so it was committed, not left in a transaction.
        Assert.Equal(1, await CountAsync($"select count(*) from console.audit_log where idempotency_key = '{key}' and phase = 'intent'"));
    }

    [Fact]
    public async Task Orphan_is_an_intent_older_than_the_limit_with_no_outcome()
    {
        await db.ResetAsync();
        await using var source = NpgsqlDataSource.Create(db.As(ConsoleDb.WriteUser, "primary"));
        var writer = new AuditWriter(source);
        await writer.WriteIntentAsync(Intent("old"), CancellationToken.None);
        await writer.WriteIntentAsync(Intent("new"), CancellationToken.None);
        await db.ExecAsync("primary", "update console.audit_log set created_at = now() - interval '11 minutes' where idempotency_key = 'old'");

        var orphans = await writer.FindOrphanIntentsAsync(TimeSpan.FromMinutes(10), CancellationToken.None);

        Assert.Equal(["old"], orphans.Select(row => row.IdempotencyKey));
    }

    [Fact]
    public async Task Intent_with_outcome_is_not_an_orphan()
    {
        await db.ResetAsync();
        await using var source = NpgsqlDataSource.Create(db.As(ConsoleDb.WriteUser, "primary"));
        var writer = new AuditWriter(source);
        var intent = Intent("done");
        await writer.WriteIntentAsync(intent, CancellationToken.None);
        await writer.WriteOutcomeAsync("done", intent.StaffId, "CAPTURED", CancellationToken.None);
        await db.ExecAsync("primary", "update console.audit_log set created_at = now() - interval '11 minutes'");

        Assert.Empty(await writer.FindOrphanIntentsAsync(TimeSpan.FromMinutes(10), CancellationToken.None));
        Assert.Equal(1, await CountAsync("select count(*) from console.audit_log where phase = 'outcome' and action = 'capture' and result = 'CAPTURED'"));
    }

    [Fact]
    public async Task Repeated_intent_for_same_key_is_kept_and_counts_once_for_orphans()
    {
        await db.ResetAsync();
        await using var source = NpgsqlDataSource.Create(db.As(ConsoleDb.WriteUser, "primary"));
        var writer = new AuditWriter(source);
        await writer.WriteIntentAsync(Intent("twice"), CancellationToken.None);
        await writer.WriteIntentAsync(Intent("twice"), CancellationToken.None);
        await db.ExecAsync("primary", "update console.audit_log set created_at = now() - interval '11 minutes'");

        Assert.Equal(2, await CountAsync("select count(*) from console.audit_log where idempotency_key = 'twice'"));
        Assert.Single(await writer.FindOrphanIntentsAsync(TimeSpan.FromMinutes(10), CancellationToken.None));
    }

    [Fact]
    public async Task Console_role_cannot_rewrite_the_audit_log()
    {
        await db.ResetAsync();
        await using var source = NpgsqlDataSource.Create(db.As(ConsoleDb.WriteUser, "primary"));
        await new AuditWriter(source).WriteIntentAsync(Intent("fixed"), CancellationToken.None);

        await using var command = source.CreateCommand("update console.audit_log set reason = 'changed'");
        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }

    private static AuditIntent Intent(string key) =>
        new(Guid.NewGuid(), "capture", Guid.NewGuid(), null, 1050, "USD", "Customer confirmed", key);

    private async Task<long> CountAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(db.AdminFor("primary"));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
