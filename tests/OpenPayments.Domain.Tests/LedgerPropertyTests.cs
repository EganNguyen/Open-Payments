using FsCheck.Xunit;
using OpenPayments.Domain;
using static OpenPayments.Domain.PaymentRules;
using Xunit;

namespace OpenPayments.Domain.Tests;

public sealed class LedgerPropertyTests
{
    private static readonly PaymentId Payment = PaymentId.Parse("00000000-0000-7000-8000-000000000011");
    private static readonly MerchantId Merchant = MerchantId.Parse("00000000-0000-7000-8000-000000000001");
    private static readonly AttemptId Attempt = AttemptId.Parse("00000000-0000-7000-8000-000000000021");
    private static readonly AttemptId CaptureAttempt = AttemptId.Parse("00000000-0000-7000-8000-000000000022");
    private static readonly AttemptId VoidAttempt = AttemptId.Parse("00000000-0000-7000-8000-000000000023");
    private static readonly RefundId Refund = RefundId.Parse("00000000-0000-7000-8000-000000000031");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-05T00:00:00Z");
    private static readonly Money Hundred = new(100, CurrencyCode.Usd);

    [Property]
    public bool U3_every_posting_balances(int selector)
    {
        var (snapshot, command) = Scenario(selector);
        var accepted = Assert.IsType<TransitionResult.Accepted>(Decide(snapshot, command, RuleVersion.V1));

        foreach (var transaction in accepted.Ledger.GroupBy(line => line.TransactionId))
        {
            foreach (var currency in transaction.GroupBy(line => line.Amount.Currency))
            {
                var debit = currency.Where(line => line.Direction == 'D').Sum(line => line.Amount.Minor);
                var credit = currency.Where(line => line.Direction == 'C').Sum(line => line.Amount.Minor);
                Assert.Equal(debit, credit);
            }
        }

        return true;
    }

    [Property]
    public bool U3_no_transaction_mixes_real_and_logical(int selector)
    {
        var (snapshot, command) = Scenario(selector);
        var accepted = Assert.IsType<TransitionResult.Accepted>(Decide(snapshot, command, RuleVersion.V1));

        foreach (var transaction in accepted.Ledger.GroupBy(line => line.TransactionId))
        {
            Assert.Single(transaction.Select(line => line.ClassOfMoney).Distinct());
        }

        return true;
    }

    [Property]
    public bool U3_every_transaction_balances_per_currency(int selector)
    {
        var (snapshot, command) = Scenario(selector);
        var accepted = Assert.IsType<TransitionResult.Accepted>(Decide(snapshot, command, RuleVersion.V1));

        var unbalanced = accepted.Ledger
            .GroupBy(line => (line.TransactionId, line.Amount.Currency))
            .Where(group =>
                group.Where(line => line.Direction == 'D').Sum(line => line.Amount.Minor)
                != group.Where(line => line.Direction == 'C').Sum(line => line.Amount.Minor));
        Assert.Empty(unbalanced);
        return true;
    }

    [Fact]
    public void U3_final_balance_matches_final_state()
    {
        var ledger = new List<LedgerLine>();
        var snapshot = new PaymentSnapshot(Payment, Merchant, PaymentState.AwaitingMethod, Hundred);

        snapshot = Apply(
            snapshot,
            new PaymentCommand.CreatePayment(
                Payment,
                Merchant,
                Hundred,
                HasMethod: true,
                AttemptOperation.Authorize,
                Now,
                AttemptId: Attempt),
            ledger);

        snapshot = Apply(snapshot, new PaymentCommand.PspAnswer(
            Attempt,
            AttemptOperation.Authorize,
            PspAnswerOutcome.Approved,
            null,
            null,
            null,
            Now), ledger);

        snapshot = Apply(snapshot, new PaymentCommand.Capture(CaptureAttempt, Hundred, Now), ledger);
        snapshot = Apply(snapshot, new PaymentCommand.CaptureAnswer(
            CaptureAttempt,
            Approved: true,
            AnswerAmount: null,
            FailureCode: null,
            TimedOut: false,
            Now), ledger);

        snapshot = Apply(snapshot, new PaymentCommand.RefundReserve(Refund, new Money(30, CurrencyCode.Usd), Now), ledger);
        snapshot = Apply(snapshot, new PaymentCommand.RefundConfirm(Refund, null, Now), ledger);

        var secondRefund = RefundId.Parse("00000000-0000-7000-8000-000000000032");
        snapshot = Apply(snapshot, new PaymentCommand.RefundReserve(secondRefund, new Money(70, CurrencyCode.Usd), Now), ledger);
        snapshot = Apply(snapshot, new PaymentCommand.RefundConfirm(secondRefund, null, Now), ledger);

        Assert.Equal(PaymentState.Refunded, snapshot.State);
        Assert.Equal(100, snapshot.RefundedMinor);
        Assert.Equal(0, snapshot.RefundPendingMinor);

        Assert.Equal(0, Balance(ledger, Posting.AuthHold));
        Assert.Equal(0, Balance(ledger, Posting.AuthHoldContra));
        Assert.Equal(0, Balance(ledger, Posting.MerchantPayable));
        Assert.Equal(0, Balance(ledger, Posting.RefundReserved));

        // These clear at settlement, which is outside Phase 1's worked example.
        Assert.Equal(100, Balance(ledger, Posting.PspReceivable));
        Assert.Equal(-100, Balance(ledger, Posting.PspRefundPayable));
    }

    private static PaymentSnapshot Apply(
        PaymentSnapshot current,
        PaymentCommand command,
        List<LedgerLine> ledger)
    {
        var accepted = Assert.IsType<TransitionResult.Accepted>(Decide(current, command, RuleVersion.V1));
        ledger.AddRange(accepted.Ledger);

        var captured = accepted.NewState is PaymentState.Captured
            or PaymentState.PartiallyRefunded
            or PaymentState.Refunded
            ? Math.Max(current.CapturedMinor, current.Amount.Minor)
            : current.CapturedMinor;

        var refunded = current.RefundedMinor;
        var pending = current.RefundPendingMinor;
        RefundSnapshot? refund = current.Refund;
        switch (command)
        {
            case PaymentCommand.RefundReserve reserve:
                pending += reserve.Amount.Minor;
                refund = new RefundSnapshot(reserve.RefundId, RefundState.Pending, reserve.Amount);
                break;
            case PaymentCommand.RefundConfirm:
                var amount = current.Refund?.Amount.Minor ?? 0;
                refunded += amount;
                pending -= amount;
                refund = current.Refund is null
                    ? null
                    : current.Refund with { State = RefundState.Succeeded };
                break;
            case PaymentCommand.RefundRelease release:
                pending -= current.Refund?.Amount.Minor ?? 0;
                refund = current.Refund is null
                    ? null
                    : current.Refund with { State = RefundState.Failed };
                break;
            case PaymentCommand.RefundTimeout:
                refund = current.Refund is null
                    ? null
                    : current.Refund with { State = RefundState.Unknown };
                break;
            case PaymentCommand.RefundMismatch:
                refund = current.Refund is null
                    ? null
                    : current.Refund with { State = RefundState.Review };
                break;
        }

        var attempt = accepted.Attempt is null
            ? current.Attempt
            : new AttemptSnapshot(
                accepted.Attempt.Id,
                accepted.Attempt.Operation,
                accepted.Attempt.State,
                current.Amount,
                accepted.Attempt.VoidTargetAttemptId,
                accepted.Attempt.FailureCode);

        return current with
        {
            State = accepted.NewState,
            CapturedMinor = captured,
            RefundedMinor = refunded,
            RefundPendingMinor = pending,
            Attempt = attempt,
            Refund = refund,
            EventSeq = current.EventSeq + accepted.Events.Count,
        };
    }

    private static long Balance(IEnumerable<LedgerLine> ledger, string account) =>
        ledger.Where(line => line.AccountType == account)
            .Sum(line => line.Direction == 'D' ? line.Amount.Minor : -line.Amount.Minor);

    private static (PaymentSnapshot Snapshot, PaymentCommand Command) Scenario(int selector) =>
        ((selector & int.MaxValue) % 9) switch
        {
            0 => (Snapshot(PaymentState.PspPending), new PaymentCommand.PspAnswer(
                Attempt, AttemptOperation.Authorize, PspAnswerOutcome.Approved, null, null, null, Now)),
            1 => (Snapshot(PaymentState.PspPending, AttemptOperation.Sale), new PaymentCommand.PspAnswer(
                Attempt, AttemptOperation.Sale, PspAnswerOutcome.Approved, null, null, null, Now)),
            2 => (Snapshot(PaymentState.Authorized), new PaymentCommand.CaptureAnswer(
                CaptureAttempt, true, null, null, false, Now)),
            3 => (Snapshot(PaymentState.Captured, captured: 100), new PaymentCommand.RefundReserve(
                Refund, new Money(30, CurrencyCode.Usd), Now)),
            4 => (Snapshot(PaymentState.Captured, captured: 100, pending: 30, refundState: RefundState.Pending, refundAmount: 30),
                new PaymentCommand.RefundConfirm(Refund, null, Now)),
            5 => (Snapshot(PaymentState.Captured, captured: 100, pending: 30, refundState: RefundState.Pending, refundAmount: 30),
                new PaymentCommand.RefundRelease(Refund, true, null, Now)),
            6 => (Snapshot(PaymentState.Authorized), new PaymentCommand.VoidAnswer(VoidAttempt, true, false, null, Now)),
            7 => (Snapshot(PaymentState.Authorized), new PaymentCommand.Expiry(Now)),
            8 => (Snapshot(PaymentState.Review), new PaymentCommand.ManualResolution(PaymentResolution.Capture, null, Now)),
            _ => throw new InvalidOperationException(),
        };

    private static PaymentSnapshot Snapshot(
        PaymentState state,
        AttemptOperation operation = AttemptOperation.Authorize,
        long captured = 0,
        long pending = 0,
        RefundState? refundState = null,
        long refundAmount = 0)
    {
        var attempt = state is PaymentState.AwaitingMethod or PaymentState.Canceled
            ? null
            : new AttemptSnapshot(
                Attempt,
                operation,
                state == PaymentState.PspPending ? AttemptState.Pending : AttemptState.Succeeded,
                Hundred);
        var refund = refundState is null
            ? null
            : new RefundSnapshot(Refund, refundState.Value, new Money(refundAmount, CurrencyCode.Usd));
        return new PaymentSnapshot(
            Payment,
            Merchant,
            state,
            Hundred,
            captured,
            RefundPendingMinor: pending,
            Attempt: attempt,
            Refund: refund,
            AttemptCount: 1);
    }
}
