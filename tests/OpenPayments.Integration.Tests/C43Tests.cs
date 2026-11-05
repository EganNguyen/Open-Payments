using Npgsql;
using OpenPayments.Domain;
using OpenPayments.Transitions;
using Xunit;

namespace OpenPayments.Integration.Tests;

[Collection("c43")]
public sealed class C43Tests(C43Fixture db)
{
    [Theory]
    [InlineData("direct")]
    [InlineData("pgbouncer")]
    public async Task C43_commit_ignores_request_cancellation(string mode)
    {
        await db.ResetAsync();
        await db.BlockAsync();
        await using var runner = new TxRunner(
            db.ConnectionString(mode), TimeSpan.FromMilliseconds(700));
        using var cancellation = new CancellationTokenSource();

        var merchant = new MerchantId(Guid.NewGuid());
        var exception = await Assert.ThrowsAsync<CommitOutcomeUnknownException>(() => runner.RunAsync(
            merchant,
            async (connection, transaction, token) =>
            {
                await WriteAsync(connection, transaction, merchant, token);
                _ = Task.Run(async () =>
                {
                    await Task.Delay(150);
                    await cancellation.CancelAsync();
                }, CancellationToken.None);
                return 1;
            },
            cancellation.Token));

        Assert.Equal("commit_wait_limit", exception.Reason);
        Assert.True(cancellation.IsCancellationRequested);
        await db.UnblockAsync(terminateBackends: false);
        await runner.WaitForAbandonedAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("pgbouncer")]
    public async Task C43_commit_wait_limit_sends_no_cancel_and_reports_unknown(string mode)
    {
        await db.ResetAsync();
        await db.BlockAsync();
        await using var runner = new TxRunner(
            db.ConnectionString(mode), TimeSpan.FromMilliseconds(500));

        var merchant = new MerchantId(Guid.NewGuid());
        var exception = await Assert.ThrowsAsync<CommitOutcomeUnknownException>(() => runner.RunAsync(
            merchant,
            (connection, transaction, token) => WriteAsync(connection, transaction, merchant, token),
            CancellationToken.None));

        Assert.Equal("commit_wait_limit", exception.Reason);
        await Task.Delay(250);
        Assert.True(
            await db.CountSyncRepWaitersAsync() >= 1,
            "the backend must still be waiting in SyncRep; a cancel would have ended the wait");
        Assert.Equal(0, runner.Metrics.CommitWarnings);
        await db.UnblockAsync(terminateBackends: false);
        await runner.WaitForAbandonedAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("pgbouncer")]
    public async Task C43_failed_commit_reports_unknown_not_failure(string mode)
    {
        await db.ResetAsync();
        await db.BlockAsync();
        await using var runner = new TxRunner(
            db.ConnectionString(mode), TimeSpan.FromSeconds(3));
        Task? killer = null;

        var merchant = new MerchantId(Guid.NewGuid());
        var exception = await Assert.ThrowsAsync<CommitOutcomeUnknownException>(() => runner.RunAsync(
            merchant,
            async (connection, transaction, cancellationToken) =>
            {
                await WriteAsync(connection, transaction, merchant, cancellationToken);
                var pid = await BackendPidAsync(connection, transaction, cancellationToken);
                killer = Task.Run(async () =>
                {
                    await Task.Delay(250);
                    await db.TerminateBackendAsync(pid);
                }, CancellationToken.None);
                return 1;
            },
            CancellationToken.None));

        await killer!;
        Assert.Equal("commit_failed", exception.Reason);
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("pgbouncer")]
    public async Task C43_commit_warning_is_treated_as_unknown_and_counted(string mode)
    {
        await db.ResetAsync();
        await db.BlockAsync();
        await using var runner = new TxRunner(
            db.ConnectionString(mode), TimeSpan.FromSeconds(3));
        Task? canceller = null;

        var merchant = new MerchantId(Guid.NewGuid());
        var exception = await Assert.ThrowsAsync<CommitOutcomeUnknownException>(() => runner.RunAsync(
            merchant,
            async (connection, transaction, cancellationToken) =>
            {
                await WriteAsync(connection, transaction, merchant, cancellationToken);
                var pid = await BackendPidAsync(connection, transaction, cancellationToken);
                canceller = Task.Run(async () =>
                {
                    await Task.Delay(250);
                    await db.CancelBackendAsync(pid);
                }, CancellationToken.None);
                return 1;
            },
            CancellationToken.None));

        await canceller!;
        Assert.Equal("commit_warning", exception.Reason);
        Assert.True(runner.Metrics.CommitWarnings >= 1);
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("pgbouncer")]
    public async Task C43_abandoned_commit_completes_later_and_releases_its_connection(string mode)
    {
        await db.ResetAsync();
        await db.BlockAsync();
        await using var runner = new TxRunner(
            db.ConnectionString(mode, maxPoolSize: 1), TimeSpan.FromMilliseconds(300));

        var merchant = new MerchantId(Guid.NewGuid());
        var exception = await Assert.ThrowsAsync<CommitOutcomeUnknownException>(() => runner.RunAsync(
            merchant,
            (connection, transaction, token) => WriteAsync(connection, transaction, merchant, token),
            CancellationToken.None));

        Assert.Equal("commit_wait_limit", exception.Reason);
        Assert.Equal(1, runner.AbandonedCommits);

        await db.UnblockAsync(terminateBackends: false);
        await runner.WaitForAbandonedAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, runner.AbandonedCommits);
        Assert.True(runner.Metrics.LateCommitSuccesses >= 1);
        Assert.Equal(7, await runner.RunAsync(
            new MerchantId(Guid.NewGuid()), (_, _, _) => Task.FromResult(7), CancellationToken.None));
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("pgbouncer")]
    public async Task C43_abandoned_commits_exhaust_the_pool_and_new_work_is_shed(string mode)
    {
        await db.ResetAsync();
        await db.BlockAsync();
        await using var runner = new TxRunner(
            db.ConnectionString(mode, maxPoolSize: 1), TimeSpan.FromMilliseconds(250));

        var merchant = new MerchantId(Guid.NewGuid());
        await Assert.ThrowsAsync<CommitOutcomeUnknownException>(() => runner.RunAsync(
            merchant,
            (connection, transaction, token) => WriteAsync(connection, transaction, merchant, token),
            CancellationToken.None));

        var busy = await Assert.ThrowsAsync<BusyException>(() => runner.RunAsync(
            new MerchantId(Guid.NewGuid()),
            (_, _, _) => Task.FromResult(1),
            CancellationToken.None));
        Assert.Equal("pool_wait", busy.Reason);

        await db.UnblockAsync(terminateBackends: false);
        await runner.WaitForAbandonedAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("pgbouncer")]
    public void C43_command_timeout_nonzero_is_refused_at_startup(string mode)
    {
        var builder = new NpgsqlConnectionStringBuilder(db.ConnectionString(mode))
        {
            CommandTimeout = 30,
        };

        var exception = Assert.Throws<InvalidOperationException>(() => new TxRunner(builder.ConnectionString));
        Assert.Contains("Command Timeout=0", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("pgbouncer")]
    public async Task C43_same_key_retry_during_abandoned_commit_returns_busy_with_no_psp_effect(string mode)
    {
        await db.ResetAsync();
        var seed = await db.SeedMerchantAsync();
        await db.BlockAsync();
        await using var runner = new TxRunner(
            db.ConnectionString(mode), TimeSpan.FromMilliseconds(300));
        var store = new PaymentStore(runner);
        var paymentId = new PaymentId(Guid.NewGuid());
        var command = new BeginAttempt(
            seed.Merchant,
            "same-key",
            [1, 2, 3],
            paymentId,
            new Money(1000, CurrencyCode.Usd),
            AttemptOperation.Authorize,
            CreatePayment: true,
            DateTimeOffset.UtcNow.AddMinutes(30),
            PspAccountId: seed.PspAccountId);

        await Assert.ThrowsAsync<CommitOutcomeUnknownException>(() => store.BeginAttemptAsync(
            command, CancellationToken.None));
        Assert.Equal(0, await db.CountAppAttemptsAsync());

        var busy = await Assert.ThrowsAsync<BusyException>(() => store.BeginAttemptAsync(
            command, CancellationToken.None));
        Assert.Equal("lock_timeout", busy.Reason);

        await db.UnblockAsync(terminateBackends: false);
        await runner.WaitForAbandonedAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, await db.CountAppAttemptsAsync());
    }

    private static async Task<int> WriteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MerchantId merchant,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            insert into idempotency_keys (merchant_id, key, request_hash)
            values (@merchant, @key, '\x00'::bytea)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("merchant", merchant.Value);
        command.Parameters.AddWithValue("key", Guid.NewGuid().ToString("N"));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> BackendPidAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("select pg_backend_pid()", connection, transaction);
        return (int)(await command.ExecuteScalarAsync(cancellationToken))!;
    }
}
