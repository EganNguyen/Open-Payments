using Microsoft.Extensions.Options;
using OpenPayments.Domain;
using OpenPayments.Psp;
using OpenPayments.Psp.Resilience;
using OpenPayments.Transitions;

namespace OpenPayments.Reconciler;

public sealed record RecoverySummary(int Claimed, int Recovered, int Skipped);

/// <summary>
/// Recovers unresolved attempts and refunds by lease, inside the recovery lane (D60, D62).
/// Recovery never starts an authorize, never replays outside the window, and never treats
/// "not found" as proof unless the adapter says its lookup is authoritative.
/// </summary>
public sealed class RecoveryWorker(
    PaymentStore store,
    RecoveryStore recovery,
    IPspAdapterFactory adapters,
    AccountGate gate,
    IOptions<ReconcilerOptions> options,
    ILogger<RecoveryWorker> logger) : BackgroundService
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
#pragma warning disable CA1031 // A failed tick is retried; the lease bounds duplicates.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                logger.LogError(exception, "The recovery tick failed.");
            }
        }
    }

    public async Task<RecoverySummary> RunOnceAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var items = await recovery.ClaimAsync(
            now, _options.Batch, _options.Lease, _options.AttemptTimeout, cancellationToken);
        var recovered = 0;
        var skipped = 0;
        foreach (var item in items)
        {
            try
            {
                var result = await RecoverOneAsync(item, now, cancellationToken);
                if (result)
                {
                    recovered++;
                }
                else
                {
                    skipped++;
                }
            }
            catch (PspRejectedException)
            {
                skipped++;
            }
#pragma warning disable CA1031 // One operation's failure leaves its lease to expire.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                logger.LogWarning(exception, "Recovering {Operation} failed.", item.Operation);
                skipped++;
            }
        }

        return new RecoverySummary(items.Count, recovered, skipped);
    }

    private async Task<bool> RecoverOneAsync(
        RecoveryItem item, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var account = new PspAccount(
            item.PspAccountId, item.Merchant, item.Psp, "test", string.Empty, 1000, 0.10m);
        using var lease = gate.Acquire(item.PspAccountId, item.Psp, Lane.Recovery, 1000, 0.10m);
        var adapter = adapters.For(account);
        var capabilities = adapters.Capabilities(account);

        if (item.AttemptState == AttemptState.Pending && item.Operation is OperationRef.Attempt pending)
        {
            await store.ApplyAsync(
                item.Payment,
                new PaymentCommand.Timeout(pending.Id, pending.Operation, now),
                null,
                cancellationToken);
        }

        PspInquiry inquiry;
        if (item.Operation is OperationRef.Refund refund)
        {
            inquiry = await adapter.RecoverRefundAsync(refund.Id, cancellationToken);
        }
        else if (item.Operation is OperationRef.Attempt attempt)
        {
            if (item.PspReference is { } reference && capabilities.ConsistentLookupByReference)
            {
                inquiry = await adapter.LookupByReferenceAsync(reference, cancellationToken);
            }
            else
            {
                switch (capabilities.Unanswered)
                {
                    case RecoveryMode.Replay:
                        if (now >= item.CreatedAt + capabilities.ReplayWindow)
                        {
                            // C45: past the window no replay is ever sent; the attempt stays UNKNOWN.
                            return false;
                        }

                        inquiry = new PspInquiry.Answered(
                            await ReplayAsync(adapter, item, attempt, cancellationToken));
                        break;
                    case RecoveryMode.TechnicalCancel:
                        inquiry = await adapter.CancelForProofAsync(
                            attempt.Id, item.PspReference, cancellationToken);
                        break;
                    default:
                        inquiry = await adapter.RecoverAsync(attempt.Id, cancellationToken);
                        break;
                }
            }
        }
        else
        {
            return false;
        }

        await recovery.MarkInquiryAsync(item.Operation, item.Payment, Describe(inquiry), cancellationToken);
        var command = ToCommand(item, inquiry, now);
        if (command is null)
        {
            return false;
        }

        return await store.ApplyPspAnswerAsync(item.Payment, command, null, cancellationToken)
            is ApplyResult.Applied;
    }

    private static Task<PspResult> ReplayAsync(
        IPspAdapter adapter, RecoveryItem item, OperationRef.Attempt attempt, CancellationToken cancellationToken)
    {
        var method = new PaymentMethodRef(item.MethodType ?? "card", item.MethodRef ?? "tok_test");
        return attempt.Operation switch
        {
            AttemptOperation.Capture => adapter.CaptureAsync(attempt.Id, item.Amount, cancellationToken),
            AttemptOperation.Void => adapter.VoidAsync(
                attempt.Id, attempt.Id, cancellationToken),
            _ => adapter.AuthorizeAsync(attempt.Id, item.Amount, method, cancellationToken),
        };
    }

    private static PaymentCommand? ToCommand(
        RecoveryItem item, PspInquiry inquiry, DateTimeOffset now)
    {
        if (item.Operation is OperationRef.Refund refund)
        {
            return inquiry switch
            {
                PspInquiry.Answered { Result: PspResult.Approved approved } =>
                    new PaymentCommand.RefundConfirm(refund.Id, approved.Amount ?? item.Amount, now),
                PspInquiry.Answered { Result: PspResult.Declined declined } =>
                    new PaymentCommand.RefundRelease(refund.Id, false, declined.FailureCode, now),
                PspInquiry.ProvenNotCharged notCharged =>
                    new PaymentCommand.RefundRelease(refund.Id, true, notCharged.FailureCode, now),
                _ => null,
            };
        }

        if (item.Operation is not OperationRef.Attempt attempt)
        {
            return null;
        }

        return inquiry switch
        {
            PspInquiry.Answered { Result: PspResult.Approved approved } =>
                new PaymentCommand.RecoveryAnswer(
                    attempt.Id, attempt.Operation, RecoveryOutcome.Approved,
                    approved.Amount ?? item.Amount, null, null, now),
            PspInquiry.Answered { Result: PspResult.Declined declined } =>
                new PaymentCommand.RecoveryAnswer(
                    attempt.Id, attempt.Operation, RecoveryOutcome.Declined,
                    null, declined.FailureCode, null, now),
            PspInquiry.Answered { Result: PspResult.RequiresAction action } =>
                new PaymentCommand.RecoveryAnswer(
                    attempt.Id, attempt.Operation, RecoveryOutcome.RequiresAction,
                    item.Amount, null, action.ActionType, now),
            PspInquiry.ProvenNotCharged notCharged =>
                new PaymentCommand.RecoveryAnswer(
                    attempt.Id, attempt.Operation, RecoveryOutcome.ProvenNotCharged,
                    null, notCharged.FailureCode, null, now),
            _ => null,
        };
    }

    private static string Describe(PspInquiry inquiry) => inquiry switch
    {
        PspInquiry.Answered { Result: PspResult.Approved } => "approved",
        PspInquiry.Answered { Result: PspResult.Declined declined } => "declined:" + declined.FailureCode,
        PspInquiry.Answered { Result: PspResult.RequiresAction } => "action_required",
        PspInquiry.ProvenNotCharged notCharged => "not_charged:" + notCharged.FailureCode,
        PspInquiry.Inconclusive inconclusive => "inconclusive:" + inconclusive.Reason,
        _ => "unknown",
    };
}
