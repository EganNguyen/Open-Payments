using Microsoft.Extensions.Options;
using Npgsql;
using OpenPayments.Domain;
using OpenPayments.Transitions;

namespace OpenPayments.Reconciler;

public sealed record EscalationSummary(int EscalatedAttempts, int EscalatedRefunds);

/// <summary>
/// D45: an unresolved authorize, sale or capture past the age limit moves with its payment to
/// REVIEW; a refund moves on its own row. A void is never escalated. The worker selects due rows
/// without a lock and applies each command through the normal conditional update, so an
/// operation resolved in the meantime is left alone.
/// </summary>
public sealed class EscalationWorker(
    PaymentStore store,
    TxRunner runner,
    IOptions<ReconcilerOptions> options,
    ILogger<EscalationWorker> logger) : BackgroundService
{
    private readonly ReconcilerOptions _options = options.Value;

    /// <summary>Test-only failpoint between the due query and the conditional update.</summary>
    public Action? BeforeApply { get; set; }

    public long EscalatedAttempts { get; private set; }

    public long EscalatedRefunds { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.Tick);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunOnceAsync(DateTimeOffset.UtcNow, stoppingToken);
            }
#pragma warning disable CA1031 // A failed tick is retried; escalations are idempotent.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                logger.LogError(exception, "The escalation tick failed.");
            }
        }
    }

    public async Task<EscalationSummary> RunOnceAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var cutoff = now - _options.EscalateAfter;
        var attempts = await DueAttemptsAsync(cutoff, cancellationToken);
        var refunds = await DueRefundsAsync(cutoff, cancellationToken);

        var escalatedAttempts = 0;
        foreach (var item in attempts)
        {
            BeforeApply?.Invoke();
            var result = await store.ApplyAsync(
                new PaymentId(item.Payment),
                new PaymentCommand.Escalate(
                    new AttemptId(item.Attempt), item.Operation, now),
                null,
                cancellationToken);
            if (result is ApplyResult.Applied)
            {
                escalatedAttempts++;
            }
        }

        var escalatedRefunds = 0;
        foreach (var item in refunds)
        {
            BeforeApply?.Invoke();
            var result = await store.ApplyAsync(
                new PaymentId(item.Payment),
                new PaymentCommand.RefundEscalate(new RefundId(item.Refund), now),
                null,
                cancellationToken);
            if (result is ApplyResult.Applied)
            {
                escalatedRefunds++;
            }
        }

        if (escalatedAttempts > 0 || escalatedRefunds > 0)
        {
            EscalatedAttempts += escalatedAttempts;
            EscalatedRefunds += escalatedRefunds;
            logger.LogWarning(
                "Escalated {Attempts} attempts and {Refunds} refunds past the age limit (D45).",
                escalatedAttempts,
                escalatedRefunds);
        }

        return new EscalationSummary(escalatedAttempts, escalatedRefunds);
    }

    private async Task<IReadOnlyList<(Guid Payment, Guid Attempt, AttemptOperation Operation)>>
        DueAttemptsAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        var due = new List<(Guid, Guid, AttemptOperation)>();
        await using var connection = await runner.DataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select a.payment_id, a.attempt_id, a.operation
              from payment_attempts a
              join payments p on p.id = a.payment_id
             where a.state = 'UNKNOWN'
               and a.operation <> 'void'
               and a.created_at <= @cutoff
               and ((a.operation in ('authorize','sale') and p.state = 'UNKNOWN')
                 or (a.operation = 'capture' and p.state = 'AUTHORIZED'))
             order by a.created_at
             limit @batch
            """,
            connection);
        command.Parameters.AddWithValue("cutoff", cutoff);
        command.Parameters.AddWithValue("batch", _options.Batch);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            due.Add((
                reader.GetGuid(0),
                reader.GetGuid(1),
                ParseOperation(reader.GetString(2))));
        }

        return due;
    }

    private static AttemptOperation ParseOperation(string text) => text switch
    {
        "authorize" => AttemptOperation.Authorize,
        "sale" => AttemptOperation.Sale,
        "capture" => AttemptOperation.Capture,
        "void" => AttemptOperation.Void,
        _ => throw new InvalidOperationException($"Unknown attempt operation: {text}"),
    };

    private async Task<IReadOnlyList<(Guid Payment, Guid Refund)>> DueRefundsAsync(
        DateTimeOffset cutoff,
        CancellationToken cancellationToken)
    {
        var due = new List<(Guid, Guid)>();
        await using var connection = await runner.DataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select id, payment_id
              from refunds
             where state = 'UNKNOWN' and created_at <= @cutoff
             order by created_at
             limit @batch
            """,
            connection);
        command.Parameters.AddWithValue("cutoff", cutoff);
        command.Parameters.AddWithValue("batch", _options.Batch);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            due.Add((reader.GetGuid(1), reader.GetGuid(0)));
        }

        return due;
    }
}
