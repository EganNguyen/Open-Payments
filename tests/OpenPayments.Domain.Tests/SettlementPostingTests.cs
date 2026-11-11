using OpenPayments.Domain;
using Xunit;
using static OpenPayments.Domain.PaymentRules;

namespace OpenPayments.Domain.Tests;

// D18/D21: a matched settlement line posts only the clearing entries and never moves the payment.
public sealed class SettlementPostingTests
{
    private static readonly PaymentId Payment = PaymentId.Parse("00000000-0000-7000-8000-000000000011");
    private static readonly MerchantId Merchant = MerchantId.Parse("00000000-0000-7000-8000-000000000001");
    private static readonly AttemptId Attempt = AttemptId.Parse("00000000-0000-7000-8000-000000000021");
    private static readonly RefundId Refund = RefundId.Parse("00000000-0000-7000-8000-000000000031");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-05T00:00:00Z");
    private static readonly Money Hundred = new(100, CurrencyCode.Usd);
    private static readonly Guid Transaction = Guid.Parse("11111111-1111-5111-8111-111111111111");

    [Fact]
    public void Settlement_of_a_capture_clears_the_receivable_and_keeps_the_state()
    {
        var result = Assert.IsType<TransitionResult.Accepted>(Decide(
            Snapshot(PaymentState.Captured, attemptState: AttemptState.Succeeded),
            new PaymentCommand.SettlementMatched(Attempt, null, Hundred, Transaction, Now),
            RuleVersion.V1));

        Assert.Equal(PaymentState.Captured, result.NewState);
        Assert.Null(result.Attempt);
        Assert.Null(result.Refund);
        Assert.Empty(result.Events);
        Assert.Equal(
            [
                ("settlement", Posting.SettlementBank, 'D', 100L),
                ("settlement", Posting.PspReceivable, 'C', 100L),
            ],
            result.Ledger.Select(line => (line.TransactionKind, line.AccountType, line.Direction, line.Amount.Minor)));
        Assert.All(result.Ledger, line => Assert.Equal(Transaction, line.TransactionId));
    }

    [Fact]
    public void Settlement_of_a_refund_clears_the_payable_and_keeps_the_state()
    {
        var result = Assert.IsType<TransitionResult.Accepted>(Decide(
            Snapshot(PaymentState.Refunded, refundState: RefundState.Succeeded),
            new PaymentCommand.SettlementMatched(null, Refund, Hundred, Transaction, Now),
            RuleVersion.V1));

        Assert.Equal(PaymentState.Refunded, result.NewState);
        Assert.Equal(
            [
                ("settlement", Posting.PspRefundPayable, 'D', 100L),
                ("settlement", Posting.SettlementBank, 'C', 100L),
            ],
            result.Ledger.Select(line => (line.TransactionKind, line.AccountType, line.Direction, line.Amount.Minor)));
    }

    [Fact]
    public void Settlement_rejects_an_attempt_that_has_not_succeeded()
    {
        var result = Assert.IsType<TransitionResult.Rejected>(Decide(
            Snapshot(PaymentState.Captured, attemptState: AttemptState.Pending),
            new PaymentCommand.SettlementMatched(Attempt, null, Hundred, Transaction, Now),
            RuleVersion.V1));

        Assert.Equal("attempt_not_settled", result.ReasonCode);
    }

    [Fact]
    public void Settlement_rejects_a_refund_that_has_not_succeeded()
    {
        var result = Assert.IsType<TransitionResult.Rejected>(Decide(
            Snapshot(PaymentState.Captured, refundState: RefundState.Pending),
            new PaymentCommand.SettlementMatched(null, Refund, Hundred, Transaction, Now),
            RuleVersion.V1));

        Assert.Equal("refund_not_settled", result.ReasonCode);
    }

    [Fact]
    public void Settlement_is_idempotent_for_the_same_transaction_id()
    {
        var first = Assert.IsType<TransitionResult.Accepted>(Decide(
            Snapshot(PaymentState.Captured, attemptState: AttemptState.Succeeded),
            new PaymentCommand.SettlementMatched(Attempt, null, Hundred, Transaction, Now),
            RuleVersion.V1));
        var second = Assert.IsType<TransitionResult.Accepted>(Decide(
            Snapshot(PaymentState.Captured, attemptState: AttemptState.Succeeded),
            new PaymentCommand.SettlementMatched(Attempt, null, Hundred, Transaction, Now),
            RuleVersion.V1));

        Assert.Equal(
            first.Ledger.Select(line => line.TransactionId),
            second.Ledger.Select(line => line.TransactionId));
    }

    private static PaymentSnapshot Snapshot(
        PaymentState state,
        AttemptState attemptState = AttemptState.Succeeded,
        RefundState? refundState = null) =>
        new(
            Payment,
            Merchant,
            state,
            Hundred,
            CapturedMinor: 100,
            Attempt: new AttemptSnapshot(Attempt, AttemptOperation.Capture, attemptState, Hundred),
            Refund: refundState is null ? null : new RefundSnapshot(Refund, refundState.Value, Hundred));
}
