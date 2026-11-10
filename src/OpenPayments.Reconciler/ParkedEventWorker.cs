using Microsoft.Extensions.Options;
using Npgsql;
using OpenPayments.Transitions;

namespace OpenPayments.Reconciler;

/// <summary>
/// Claims parked PSP events with a short lease and re-applies them (D59, Task 26). An event past
/// the parked limit expires and is left to the operation's own recovery.
/// </summary>
public sealed class ParkedEventWorker(
    PaymentStore store,
    TxRunner runner,
    IOptions<ReconcilerOptions> options,
    ILogger<ParkedEventWorker> logger) : BackgroundService
{
    private readonly ReconcilerOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.Tick);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunOnceAsync(DateTimeOffset.UtcNow, stoppingToken);
            }
#pragma warning disable CA1031 // A worker tick must not stop the host; the next tick retries.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                logger.LogError(exception, "The parked-event tick failed.");
            }
        }
    }

    public async Task<int> RunOnceAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var claimed = await ClaimAsync(now, cancellationToken);
        var applied = 0;
        foreach (var (account, eventId) in claimed)
        {
            try
            {
                var outcome = await store.Events.ReplayAsync(
                    account, eventId, now, _options.ParkedLimit, cancellationToken);
                if (outcome is EventOutcome.Applied or EventOutcome.Contradiction or EventOutcome.Expired)
                {
                    applied++;
                }
            }
#pragma warning disable CA1031 // One event's failure leaves its lease to expire; the tick continues.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                logger.LogWarning(exception, "Replaying {EventId} failed.", eventId);
            }
        }

        return applied;
    }

    private async Task<IReadOnlyList<(Guid Account, string EventId)>> ClaimAsync(
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await runner.DataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            with due as (
              select psp_account_id, psp_event_id
              from psp_events
              where processed_at is null and next_attempt_at <= @now
              order by next_attempt_at
              limit @batch
              for update skip locked)
            update psp_events e
               set next_attempt_at = @lease, attempts = e.attempts + 1
              from due
             where e.psp_account_id = due.psp_account_id and e.psp_event_id = due.psp_event_id
            returning e.psp_account_id, e.psp_event_id
            """,
            connection);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("lease", now + _options.Lease);
        command.Parameters.AddWithValue("batch", _options.Batch);
        var claimed = new List<(Guid, string)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            claimed.Add((reader.GetGuid(0), reader.GetString(1)));
        }

        return claimed;
    }
}
