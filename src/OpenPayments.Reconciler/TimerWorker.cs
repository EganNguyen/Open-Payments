using Microsoft.Extensions.Options;
using OpenPayments.Transitions;

namespace OpenPayments.Reconciler;

/// <summary>
/// Task 28: the action window, the payment window and the authorization window. The store's
/// batch runs the same conditional updates as any writer, so a timer never races a request
/// into two outcomes. An expired action goes to UNKNOWN, never FAILED; an expired authorization
/// releases its hold, but not while a capture or void is live.
/// </summary>
public sealed class TimerWorker(
    PaymentStore store,
    IOptions<ReconcilerOptions> options,
    ILogger<TimerWorker> logger) : BackgroundService
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
#pragma warning disable CA1031 // A failed tick is retried; the conditional updates stay safe.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                logger.LogError(exception, "The timer tick failed.");
            }
        }
    }

    public async Task<TimerSummary> RunOnceAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var summary = await store.RunTimersAsync(now, _options.Batch, cancellationToken);
        if (summary != new TimerSummary(0, 0, 0))
        {
            logger.LogInformation(
                "Timers: {Actions} action windows, {Payments} payment windows, {Authorizations} authorizations.",
                summary.ExpiredActions,
                summary.CanceledPayments,
                summary.ExpiredAuthorizations);
        }

        return summary;
    }
}
