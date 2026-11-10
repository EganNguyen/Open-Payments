using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenPayments.Reconciler;
using OpenPayments.Transitions;
using Xunit;

namespace OpenPayments.Reconciler.Tests;

[Collection("reconciler-postgres")]
public sealed class ExpiryTests(ReconcilerFixture db)
{
    [Fact]
    public async Task Resolved_key_past_retention_is_removed()
    {
        await db.ResetAsync();
        var (merchant, _, _, _) = await db.SeedPendingAsync();
        await InsertKeyAsync(merchant, "old-key", "resolved_at = now() - interval '25 hours'");
        await using var runner = new TxRunner(db.AppConnectionString);
        using var worker = Worker(runner);

        var summary = await worker.RunOnceAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.True(summary.KeysDeleted >= 1);
        Assert.Equal(0, await db.CountAsync(
            $"select count(*) from idempotency_keys where merchant_id = '{merchant}' and key = 'old-key'"));
        Assert.Equal(0, worker.ExpirableBacklog);
    }

    [Fact]
    public async Task Resolved_key_just_inside_retention_is_kept()
    {
        await db.ResetAsync();
        var (merchant, _, _, _) = await db.SeedPendingAsync();
        await InsertKeyAsync(merchant, "fresh-key", "resolved_at = now() - interval '23 hours'");
        await using var runner = new TxRunner(db.AppConnectionString);
        using var worker = Worker(runner);

        var summary = await worker.RunOnceAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(0, summary.KeysDeleted);
        Assert.Equal(1, await db.CountAsync(
            $"select count(*) from idempotency_keys where merchant_id = '{merchant}' and key = 'fresh-key'"));
    }

    [Fact]
    public async Task Unresolved_key_is_kept_whatever_its_age()
    {
        await db.ResetAsync();
        var (merchant, _, _, _) = await db.SeedPendingAsync();
        await InsertKeyAsync(merchant, "unknown-key", "created_at = now() - interval '72 hours'");
        await using var runner = new TxRunner(db.AppConnectionString);
        using var worker = Worker(runner);

        var summary = await worker.RunOnceAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(0, summary.KeysDeleted);
        Assert.Equal(1, await db.CountAsync(
            $"select count(*) from idempotency_keys where merchant_id = '{merchant}' and key = 'unknown-key'"));
    }

    [Fact]
    public async Task Processed_events_expire_but_parked_events_are_never_deleted()
    {
        await db.ResetAsync();
        var (_, account, _, attempt) = await db.SeedPendingAsync();
        await db.InsertCallbackEventAsync(account, attempt, "evt_processed", DateTimeOffset.UtcNow);
        await db.InsertCallbackEventAsync(account, attempt, "evt_parked", DateTimeOffset.UtcNow);
        await db.ExecAdminAsync($"""
            update psp_events set processed_at = now() - interval '8 days', outcome = 'applied'
             where psp_event_id = 'evt_processed';
            update psp_events set received_at = now() - interval '8 days'
             where psp_event_id = 'evt_parked';
            """);
        await using var runner = new TxRunner(db.AppConnectionString);
        using var worker = Worker(runner);

        var summary = await worker.RunOnceAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.True(summary.EventsDeleted >= 1);
        Assert.Equal(0, await db.CountAsync(
            "select count(*) from psp_events where psp_event_id = 'evt_processed'"));
        Assert.Equal(1, await db.CountAsync(
            "select count(*) from psp_events where psp_event_id = 'evt_parked'"));
    }

    [Fact]
    public async Task Expiry_respects_max_deletes_per_second()
    {
        await db.ResetAsync();
        var (merchant, _, _, _) = await db.SeedPendingAsync();
        for (var i = 0; i < 10; i++)
        {
            await InsertKeyAsync(
                merchant, $"paced-{i}", "resolved_at = now() - interval '25 hours'");
        }

        await using var runner = new TxRunner(db.AppConnectionString);
        var options = Options.Create(new ReconcilerOptions
        {
            ExpiryBatch = 5,
            ExpiryMaxDeletesPerSecond = 20,
        });
        using var worker = new ExpiryWorker(runner, options, NullLogger<ExpiryWorker>.Instance);
        var watch = Stopwatch.StartNew();

        var summary = await worker.RunOnceAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        watch.Stop();
        Assert.True(summary.KeysDeleted >= 10);
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(400));
    }

    private static ExpiryWorker Worker(TxRunner runner) =>
        new(runner, Options.Create(new ReconcilerOptions()), NullLogger<ExpiryWorker>.Instance);

    private async Task InsertKeyAsync(Guid merchant, string key, string assignment)
    {
        await db.ExecAdminAsync(
            $"""
            insert into idempotency_keys (merchant_id, key, request_hash) values ('{merchant}', '{key}', '\x00');
            update idempotency_keys set {assignment} where merchant_id = '{merchant}' and key = '{key}';
            """);
    }
}
