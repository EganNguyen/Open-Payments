using System.Diagnostics;
using Microsoft.Extensions.Options;
using Npgsql;
using OpenPayments.Transitions;

namespace OpenPayments.Reconciler;

public sealed record ExpirySummary(long KeysDeleted, long EventsDeleted);

/// <summary>
/// D19/D64: expire resolved keys and processed events partition by partition, oldest first, in
/// short batches with <c>for update skip locked</c>. The delete predicate itself can't match a
/// null, so an unresolved key or a parked event is never removed, whatever else happens. The
/// rate cap keeps expiry from competing with payments for WAL and disk.
/// </summary>
public sealed class ExpiryWorker(
    TxRunner runner,
    IOptions<ReconcilerOptions> options,
    ILogger<ExpiryWorker> logger) : BackgroundService
{
    private readonly ReconcilerOptions _options = options.Value;

    public long KeysExpired { get; private set; }

    public long EventsExpired { get; private set; }

    public double LastBatchSeconds { get; private set; }

    public long ExpirableBacklog { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.Tick);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunOnceAsync(DateTimeOffset.UtcNow, stoppingToken);
            }
#pragma warning disable CA1031 // A failed tick is retried; a lost batch deletes nothing.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                logger.LogError(exception, "The expiry tick failed.");
            }
        }
    }

    public async Task<ExpirySummary> RunOnceAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var keyCutoff = now - _options.KeyRetention;
        var eventCutoff = now - _options.EventRetention;

        // 0004 grants delete on the parents, not on each partition, so the batches run through
        // the parent. The partial indexes still serve each partition's oldest-first selection.
        var keys = await DeleteAsync(
            "idempotency_keys",
            "merchant_id, key",
            "resolved_at",
            keyCutoff,
            cancellationToken);
        var events = await DeleteAsync(
            "psp_events",
            "psp_account_id, psp_event_id",
            "processed_at",
            eventCutoff,
            cancellationToken);

        KeysExpired += keys;
        EventsExpired += events;
        ExpirableBacklog = await BacklogAsync(keyCutoff, cancellationToken);
        if (keys > 0 || events > 0)
        {
            logger.LogInformation(
                "Expired {Keys} keys and {Events} events; {Backlog} keys remain expirable.",
                keys,
                events,
                ExpirableBacklog);
        }

        return new ExpirySummary(keys, events);
    }

    private async Task<long> DeleteAsync(
        string table,
        string keyColumns,
        string timeColumn,
        DateTimeOffset cutoff,
        CancellationToken cancellationToken)
    {
        var deleted = 0L;
        while (true)
        {
            var watch = Stopwatch.StartNew();
            int batch;
            await using (var connection = await runner.DataSource.OpenConnectionAsync(cancellationToken))
            await using (var command = new NpgsqlCommand(
                $"""
                delete from {Quote(table)}
                 where ({keyColumns}) in (
                   select {keyColumns} from {Quote(table)}
                    where {timeColumn} is not null and {timeColumn} < @cutoff
                    order by {timeColumn}
                    limit @batch
                    for update skip locked)
                """,
                connection))
            {
                command.Parameters.AddWithValue("cutoff", cutoff);
                command.Parameters.AddWithValue("batch", _options.ExpiryBatch);
                batch = await command.ExecuteNonQueryAsync(cancellationToken);
            }

            LastBatchSeconds = watch.Elapsed.TotalSeconds;
            deleted += batch;
            if (batch == 0)
            {
                break;
            }

            var required = TimeSpan.FromSeconds(batch / _options.ExpiryMaxDeletesPerSecond);
            if (watch.Elapsed < required)
            {
                await Task.Delay(required - watch.Elapsed, cancellationToken);
            }
        }

        return deleted;
    }

    private async Task<long> BacklogAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        await using var connection = await runner.DataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "select count(*) from idempotency_keys where resolved_at is not null and resolved_at < @cutoff",
            connection);
        command.Parameters.AddWithValue("cutoff", cutoff);
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static string Quote(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
