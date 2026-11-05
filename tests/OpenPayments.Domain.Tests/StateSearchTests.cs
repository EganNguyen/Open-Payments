using OpenPayments.Domain;
using static OpenPayments.Domain.PaymentRules;
using Xunit;

namespace OpenPayments.Domain.Tests;

public sealed class StateSearchTests
{
    private static readonly PaymentId Payment = PaymentId.Parse("00000000-0000-7000-8000-000000000011");
    private static readonly MerchantId Merchant = MerchantId.Parse("00000000-0000-7000-8000-000000000001");
    private static readonly AttemptId Attempt = AttemptId.Parse("00000000-0000-7000-8000-000000000021");
    private static readonly AttemptId CaptureAttempt = AttemptId.Parse("00000000-0000-7000-8000-000000000022");
    private static readonly AttemptId VoidAttempt = AttemptId.Parse("00000000-0000-7000-8000-000000000023");
    private static readonly RefundId Refund = RefundId.Parse("00000000-0000-7000-8000-000000000031");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-05T00:00:00Z");
    private static readonly Money Hundred = new(100, CurrencyCode.Usd);

    [Fact]
    public void U2_exhaustive_search_holds_invariants_at_every_step()
    {
        var starts = new[]
        {
            Snapshot(PaymentState.AwaitingMethod),
            Snapshot(PaymentState.PspPending),
            Snapshot(PaymentState.PspPending, AttemptOperation.Sale),
            Snapshot(PaymentState.ActionRequired),
            Snapshot(PaymentState.Unknown),
            Snapshot(PaymentState.Authorized),
            Snapshot(PaymentState.Captured, captured: 100),
            Snapshot(PaymentState.PartiallyRefunded, captured: 100, refunded: 30),
            Snapshot(PaymentState.Review),
        };
        var commands = Commands();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<Node>();

        foreach (var start in starts)
        {
            queue.Enqueue(new Node(start, SeedLedger(start.State), 0, 0, 0, $"start:{start.State}"));
        }

        var examined = 0;
        while (queue.Count > 0 && examined < 5000)
        {
            var node = queue.Dequeue();
            foreach (var command in commands)
            {
                var result = Decide(node.Snapshot, command, RuleVersion.V1);
                if (result is not TransitionResult.Accepted accepted)
                {
                    continue;
                }

                examined++;
                AssertBalanced(accepted.Ledger);

                var next = Apply(node.Snapshot, command, accepted);
                Assert.True(
                    next.RefundedMinor + next.RefundPendingMinor <= next.CapturedMinor,
                    "refunded + pending exceeded captured");
                Assert.True(next.AttemptCount <= 20, "attempt count ran away");

                var success = node with
                {
                    Snapshot = next,
                    Ledger = node.Ledger.Concat(accepted.Ledger).ToList(),
                    SuccessfulAuthorize = node.SuccessfulAuthorize
                        + (accepted.Attempt is { State: AttemptState.Succeeded, Operation: AttemptOperation.Authorize or AttemptOperation.Sale } ? 1 : 0),
                    SuccessfulCapture = node.SuccessfulCapture
                        + (accepted.Attempt is { State: AttemptState.Succeeded, Operation: AttemptOperation.Capture or AttemptOperation.Sale } ? 1 : 0),
                    Depth = node.Depth + 1,
                    Path = node.Path + " > " + command.GetType().Name,
                };

                Assert.True(success.SuccessfulAuthorize <= 1, "more than one successful authorize");
                Assert.True(success.SuccessfulCapture <= 1, "more than one successful capture");

                if (next.IsFinal)
                {
                    Assert.True(
                        NetAuthHold(success.Ledger) == 0,
                        $"final {next.State} left auth_hold {NetAuthHold(success.Ledger)} on path {success.Path}");
                    foreach (var afterFinal in commands)
                    {
                        Assert.IsNotType<TransitionResult.Accepted>(
                            Decide(next, afterFinal, RuleVersion.V1));
                    }

                    continue;
                }

                if (success.Depth >= 4)
                {
                    continue;
                }

                var key = Key(next);
                if (visited.Add(key))
                {
                    queue.Enqueue(success);
                }
            }
        }

        Assert.True(examined > 50, "the search did not exercise enough transitions");
    }

    private static PaymentSnapshot Apply(
        PaymentSnapshot current,
        PaymentCommand command,
        TransitionResult.Accepted accepted)
    {
        var captured = accepted.NewState is PaymentState.Captured
            or PaymentState.PartiallyRefunded
            or PaymentState.Refunded
            ? Math.Max(current.CapturedMinor, current.Amount.Minor)
            : current.CapturedMinor;
        var refunded = current.RefundedMinor;
        var pending = current.RefundPendingMinor;
        var attempt = accepted.Attempt is null
            ? current.Attempt
            : new AttemptSnapshot(
                accepted.Attempt.Id,
                accepted.Attempt.Operation,
                accepted.Attempt.State,
                current.Amount,
                accepted.Attempt.VoidTargetAttemptId,
                accepted.Attempt.FailureCode);
        var refund = accepted.Refund is null
            ? current.Refund
            : new RefundSnapshot(
                accepted.Refund.Id,
                accepted.Refund.State,
                current.Refund?.Amount ?? current.Amount,
                accepted.Refund.FailureCode);

        switch (command)
        {
            case PaymentCommand.RefundReserve reserve:
                pending += reserve.Amount.Minor;
                refund = new RefundSnapshot(reserve.RefundId, RefundState.Pending, reserve.Amount);
                break;
            case PaymentCommand.RefundConfirm:
                var amount = current.Refund?.Amount.Minor ?? 0;
                refunded += amount;
                pending = Math.Max(0, pending - amount);
                break;
            case PaymentCommand.RefundRelease:
                pending = Math.Max(0, pending - (current.Refund?.Amount.Minor ?? 0));
                break;
        }

        return current with
        {
            State = accepted.NewState,
            CapturedMinor = captured,
            RefundedMinor = refunded,
            RefundPendingMinor = pending,
            Attempt = attempt,
            Refund = refund,
            AttemptCount = current.AttemptCount + (command is PaymentCommand.Attempt or PaymentCommand.Sale or PaymentCommand.FraudBlock ? 1 : 0),
            EventSeq = current.EventSeq + accepted.Events.Count,
            LiveCapture = command is PaymentCommand.Capture
                || (current.LiveCapture && command is not PaymentCommand.CaptureAnswer),
            LiveVoid = command is PaymentCommand.Void
                || (current.LiveVoid && command is not PaymentCommand.VoidAnswer),
            HoldPosted = accepted.NewState switch
            {
                PaymentState.Authorized => true,
                PaymentState.Captured or PaymentState.PartiallyRefunded
                    or PaymentState.Refunded or PaymentState.Voided
                    or PaymentState.Expired or PaymentState.Canceled => false,
                _ => current.HoldPosted,
            },
        };
    }

    private static List<LedgerLine> SeedLedger(PaymentState state)
    {
        var ledger = new List<LedgerLine>();
        if (state is PaymentState.Authorized or PaymentState.Captured or PaymentState.PartiallyRefunded)
        {
            ledger.AddRange(Posting.AuthorizationHold(Attempt, Hundred));
        }

        if (state is PaymentState.Captured or PaymentState.PartiallyRefunded)
        {
            ledger.AddRange(Posting.HoldRelease(Attempt, Hundred, "capture"));
            ledger.AddRange(Posting.CaptureMoney(Attempt, Hundred));
        }

        return ledger;
    }

    private static void AssertBalanced(IEnumerable<LedgerLine> ledger)
    {
        foreach (var transaction in ledger.GroupBy(line => line.TransactionId))
        {
            foreach (var currency in transaction.GroupBy(line => line.Amount.Currency))
            {
                Assert.Equal(
                    currency.Where(line => line.Direction == 'D').Sum(line => line.Amount.Minor),
                    currency.Where(line => line.Direction == 'C').Sum(line => line.Amount.Minor));
            }
        }
    }

    private static long NetAuthHold(IEnumerable<LedgerLine> ledger) =>
        ledger.Where(line => line.AccountType == Posting.AuthHold)
            .Sum(line => line.Direction == 'D' ? line.Amount.Minor : -line.Amount.Minor);

    private static string Key(PaymentSnapshot snapshot) =>
        string.Join(
            ':',
            snapshot.State,
            snapshot.CapturedMinor,
            snapshot.RefundedMinor,
            snapshot.RefundPendingMinor,
            snapshot.Attempt?.State,
            snapshot.Attempt?.Operation,
            snapshot.Refund?.State,
            snapshot.AttemptCount,
            snapshot.LiveCapture,
            snapshot.LiveVoid);

    private static IEnumerable<PaymentCommand> Commands()
    {
        yield return new PaymentCommand.Attempt(Attempt, Now);
        yield return new PaymentCommand.Sale(Attempt, Now);
        yield return new PaymentCommand.FraudBlock(Attempt, "fraud_blocked", Now);
        yield return new PaymentCommand.PspAnswer(
            Attempt, AttemptOperation.Authorize, PspAnswerOutcome.Approved, null, null, null, Now);
        yield return new PaymentCommand.PspAnswer(
            Attempt, AttemptOperation.Sale, PspAnswerOutcome.Approved, null, null, null, Now);
        yield return new PaymentCommand.PspAnswer(
            Attempt, AttemptOperation.Authorize, PspAnswerOutcome.Declined, null, "declined", null, Now);
        yield return new PaymentCommand.PspAnswer(
            Attempt, AttemptOperation.Authorize, PspAnswerOutcome.RequiresAction, null, null, "three_d_secure", Now);
        yield return new PaymentCommand.PspAnswer(
            Attempt, AttemptOperation.Authorize, PspAnswerOutcome.Approved, new Money(99, CurrencyCode.Usd), null, null, Now);
        yield return new PaymentCommand.RecoveryAnswer(
            Attempt, AttemptOperation.Authorize, RecoveryOutcome.Approved, null, null, null, Now);
        yield return new PaymentCommand.RecoveryAnswer(
            Attempt, AttemptOperation.Authorize, RecoveryOutcome.Declined, null, "declined", null, Now);
        yield return new PaymentCommand.RecoveryAnswer(
            Attempt, AttemptOperation.Authorize, RecoveryOutcome.ProvenNotCharged, null, null, null, Now);
        yield return new PaymentCommand.RecoveryAnswer(
            Attempt, AttemptOperation.Authorize, RecoveryOutcome.Inconclusive, null, null, null, Now);
        yield return new PaymentCommand.Timeout(Attempt, AttemptOperation.Authorize, Now);
        yield return new PaymentCommand.ActionCompleted(Attempt, Now);
        yield return new PaymentCommand.Capture(CaptureAttempt, Hundred, Now);
        yield return new PaymentCommand.CaptureAnswer(CaptureAttempt, true, null, null, false, Now);
        yield return new PaymentCommand.CaptureAnswer(CaptureAttempt, false, null, "declined", false, Now);
        yield return new PaymentCommand.CaptureAnswer(CaptureAttempt, true, null, null, true, Now);
        yield return new PaymentCommand.CaptureAnswer(CaptureAttempt, true, new Money(99, CurrencyCode.Usd), null, false, Now);
        yield return new PaymentCommand.RefundReserve(Refund, new Money(30, CurrencyCode.Usd), Now);
        yield return new PaymentCommand.RefundConfirm(Refund, null, Now);
        yield return new PaymentCommand.RefundRelease(Refund, true, null, Now);
        yield return new PaymentCommand.RefundTimeout(Refund, Now);
        yield return new PaymentCommand.RefundMismatch(Refund, new Money(29, CurrencyCode.Usd), Now);
        yield return new PaymentCommand.Void(VoidAttempt, Attempt, Now);
        yield return new PaymentCommand.VoidAnswer(VoidAttempt, true, false, null, Now);
        yield return new PaymentCommand.VoidAnswer(VoidAttempt, false, false, "declined", Now);
        yield return new PaymentCommand.VoidAnswer(VoidAttempt, false, true, null, Now);
        yield return new PaymentCommand.Expiry(Now);
        yield return new PaymentCommand.Cancel(Now);
        yield return new PaymentCommand.ManualResolution(PaymentResolution.Authorize, null, Now);
        yield return new PaymentCommand.ManualResolution(PaymentResolution.AwaitingMethod, null, Now);
        yield return new PaymentCommand.ManualResolution(PaymentResolution.Capture, null, Now);
        yield return new PaymentCommand.LateApproval(Attempt, AttemptOperation.Authorize, null, null, Now);
        yield return new PaymentCommand.LateApproval(Attempt, AttemptOperation.Sale, null, null, Now);
    }

    private static PaymentSnapshot Snapshot(
        PaymentState state,
        AttemptOperation operation = AttemptOperation.Authorize,
        long captured = 0,
        long refunded = 0)
    {
        var attempt = state is PaymentState.AwaitingMethod or PaymentState.Canceled
            ? null
            : new AttemptSnapshot(
                Attempt,
                operation,
                state switch
                {
                    PaymentState.PspPending => AttemptState.Pending,
                    PaymentState.ActionRequired => AttemptState.ActionRequired,
                    PaymentState.Unknown => AttemptState.Unknown,
                    PaymentState.Review => AttemptState.Review,
                    _ => AttemptState.Succeeded,
                },
                Hundred);
        var refund = state == PaymentState.PartiallyRefunded
            ? new RefundSnapshot(Refund, RefundState.Pending, new Money(70, CurrencyCode.Usd))
            : null;
        return new PaymentSnapshot(
            Payment,
            Merchant,
            state,
            Hundred,
            captured,
            refunded,
            refund is null ? 0 : 70,
            AttemptCount: 1,
            Attempt: attempt,
            Refund: refund,
            HoldPosted: state == PaymentState.Authorized);
    }

    private sealed record Node(
        PaymentSnapshot Snapshot,
        IReadOnlyList<LedgerLine> Ledger,
        int SuccessfulAuthorize,
        int SuccessfulCapture,
        int Depth,
        string Path);
}
