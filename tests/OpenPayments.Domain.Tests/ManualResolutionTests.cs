using OpenPayments.Domain;
using Xunit;
using static OpenPayments.Domain.PaymentRules;

namespace OpenPayments.Domain.Tests;

// Console Task C3: escalation, manual resolution and late-approval reversal move only what the
// table allows, and the attempt state always matches the resolution that was chosen.
public sealed class ManualResolutionTests
{
    private static readonly PaymentId Payment = PaymentId.Parse("00000000-0000-7000-8000-000000000011");
    private static readonly MerchantId Merchant = MerchantId.Parse("00000000-0000-7000-8000-000000000001");
    private static readonly AttemptId Attempt = AttemptId.Parse("00000000-0000-7000-8000-000000000021");
    private static readonly RefundId Refund = RefundId.Parse("00000000-0000-7000-8000-000000000031");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-05T00:00:00Z");
    private static readonly Money Hundred = new(100, CurrencyCode.Usd);

    [Fact]
    public void Escalate_unknown_sale_moves_payment_and_attempt_to_review_with_no_ledger()
    {
        var result = Assert.IsType<TransitionResult.Accepted>(Decide(
            Snapshot(PaymentState.Unknown, AttemptOperation.Sale, AttemptState.Unknown),
            new PaymentCommand.Escalate(Attempt, AttemptOperation.Sale, Now),
            RuleVersion.V1));

        Assert.Equal(PaymentState.Review, result.NewState);
        Assert.Equal(AttemptState.Review, result.Attempt!.State);
        Assert.Empty(result.Ledger);
    }

    [Fact]
    public void Escalate_unknown_capture_moves_authorized_payment_to_review_and_keeps_hold()
    {
        var result = Assert.IsType<TransitionResult.Accepted>(Decide(
            Snapshot(PaymentState.Authorized, AttemptOperation.Capture, AttemptState.Unknown),
            new PaymentCommand.Escalate(Attempt, AttemptOperation.Capture, Now),
            RuleVersion.V1));

        Assert.Equal(PaymentState.Review, result.NewState);
        Assert.Equal(AttemptState.Review, result.Attempt!.State);
        Assert.Empty(result.Ledger);
    }

    [Fact]
    public void Escalate_void_is_rejected()
    {
        var result = Assert.IsType<TransitionResult.Rejected>(Decide(
            Snapshot(PaymentState.Authorized, AttemptOperation.Void, AttemptState.Unknown),
            new PaymentCommand.Escalate(Attempt, AttemptOperation.Void, Now),
            RuleVersion.V1));

        Assert.Equal("invalid_operation", result.ReasonCode);
    }

    [Fact]
    public void Escalate_rejects_an_attempt_that_is_not_unknown()
    {
        var result = Assert.IsType<TransitionResult.Rejected>(Decide(
            Snapshot(PaymentState.Unknown, AttemptOperation.Sale, AttemptState.Pending),
            new PaymentCommand.Escalate(Attempt, AttemptOperation.Sale, Now),
            RuleVersion.V1));

        Assert.Equal("attempt_not_unknown", result.ReasonCode);
    }

    [Fact]
    public void Recovery_answer_on_escalated_review_does_not_move_payment()
    {
        var result = Assert.IsType<TransitionResult.Rejected>(Decide(
            Snapshot(PaymentState.Review, AttemptOperation.Authorize, AttemptState.Review),
            new PaymentCommand.RecoveryAnswer(
                Attempt, AttemptOperation.Authorize, RecoveryOutcome.Approved, Hundred, null, null, Now),
            RuleVersion.V1));

        Assert.Equal("invalid_state", result.ReasonCode);
    }

    [Fact]
    public void RefundEscalate_keeps_payment_state_and_reserved_amount()
    {
        var result = Assert.IsType<TransitionResult.Accepted>(Decide(
            Snapshot(PaymentState.Captured, refundState: RefundState.Unknown),
            new PaymentCommand.RefundEscalate(Refund, Now),
            RuleVersion.V1));

        Assert.Equal(PaymentState.Captured, result.NewState);
        Assert.Equal(RefundState.Review, result.Refund!.State);
        Assert.Empty(result.Ledger);
    }

    [Theory]
    [InlineData(AttemptOperation.Authorize, PaymentResolution.Authorize, AttemptState.Succeeded, false)]
    [InlineData(AttemptOperation.Authorize, PaymentResolution.AwaitingMethod, AttemptState.Failed, false)]
    [InlineData(AttemptOperation.Authorize, PaymentResolution.Capture, AttemptState.Succeeded, false)]
    [InlineData(AttemptOperation.Sale, PaymentResolution.Authorize, AttemptState.Unknown, true)]
    [InlineData(AttemptOperation.Sale, PaymentResolution.AwaitingMethod, AttemptState.Failed, false)]
    [InlineData(AttemptOperation.Sale, PaymentResolution.Capture, AttemptState.Succeeded, false)]
    [InlineData(AttemptOperation.Capture, PaymentResolution.Authorize, AttemptState.Failed, false)]
    [InlineData(AttemptOperation.Capture, PaymentResolution.AwaitingMethod, AttemptState.Unknown, true)]
    [InlineData(AttemptOperation.Capture, PaymentResolution.Capture, AttemptState.Succeeded, false)]
    public void ManualResolution_sets_attempt_state(
        AttemptOperation operation,
        PaymentResolution resolution,
        AttemptState expected,
        bool rejected)
    {
        var result = Decide(
            Snapshot(PaymentState.Review, operation, AttemptState.Review),
            new PaymentCommand.ManualResolution(resolution, null, Now),
            RuleVersion.V1);

        if (rejected)
        {
            Assert.Equal("invalid_resolution", Assert.IsType<TransitionResult.Rejected>(result).ReasonCode);
            return;
        }

        var accepted = Assert.IsType<TransitionResult.Accepted>(result);
        Assert.Equal(expected, accepted.Attempt!.State);
        if (expected == AttemptState.Failed)
        {
            Assert.Equal("manual_resolution", accepted.Attempt.FailureCode);
        }
    }

    [Fact]
    public void ManualRefundResolution_confirm_posts_same_entries_as_refund_confirm()
    {
        var manual = Assert.IsType<TransitionResult.Accepted>(Decide(
            Snapshot(PaymentState.Captured, refundState: RefundState.Review),
            new PaymentCommand.ManualRefundResolution(Refund, RefundResolution.Confirm, Now),
            RuleVersion.V1));
        var normal = Assert.IsType<TransitionResult.Accepted>(Decide(
            Snapshot(PaymentState.Captured, refundState: RefundState.Pending),
            new PaymentCommand.RefundConfirm(Refund, null, Now),
            RuleVersion.V1));

        Assert.Equal(normal.NewState, manual.NewState);
        Assert.Equal(RefundState.Succeeded, manual.Refund!.State);
        Assert.Equal(Entries(normal.Ledger), Entries(manual.Ledger));
    }

    [Fact]
    public void ManualRefundResolution_release_posts_same_entries_as_refund_release()
    {
        var manual = Assert.IsType<TransitionResult.Accepted>(Decide(
            Snapshot(PaymentState.Captured, refundState: RefundState.Review),
            new PaymentCommand.ManualRefundResolution(Refund, RefundResolution.Release, Now),
            RuleVersion.V1));
        var normal = Assert.IsType<TransitionResult.Accepted>(Decide(
            Snapshot(PaymentState.Captured, refundState: RefundState.Pending),
            new PaymentCommand.RefundRelease(Refund, ProvenNotCharged: true, null, Now),
            RuleVersion.V1));

        Assert.Equal(PaymentState.Captured, manual.NewState);
        Assert.Equal(RefundState.Failed, manual.Refund!.State);
        Assert.Equal(Entries(normal.Ledger), Entries(manual.Ledger));
    }

    [Fact]
    public void ManualRefundResolution_rejects_a_refund_not_in_review()
    {
        var result = Assert.IsType<TransitionResult.Rejected>(Decide(
            Snapshot(PaymentState.Captured, refundState: RefundState.Pending),
            new PaymentCommand.ManualRefundResolution(Refund, RefundResolution.Confirm, Now),
            RuleVersion.V1));

        Assert.Equal("refund_not_found", result.ReasonCode);
    }

    [Fact]
    public void LateApprovalReversed_writes_no_ledger_and_returns_review_payment_to_awaiting()
    {
        var result = Assert.IsType<TransitionResult.Accepted>(Decide(
            Snapshot(PaymentState.Review, AttemptOperation.Sale, AttemptState.Review),
            new PaymentCommand.LateApprovalReversed(Attempt, Now),
            RuleVersion.V1));

        Assert.Equal(PaymentState.AwaitingMethod, result.NewState);
        Assert.Equal(AttemptState.Failed, result.Attempt!.State);
        Assert.Equal("late_approval_reversed", result.Attempt.FailureCode);
        Assert.Empty(result.Ledger);
    }

    [Fact]
    public void LateApprovalReversed_rejects_an_authorize_attempt()
    {
        var result = Assert.IsType<TransitionResult.Rejected>(Decide(
            Snapshot(PaymentState.Review, AttemptOperation.Authorize, AttemptState.Review),
            new PaymentCommand.LateApprovalReversed(Attempt, Now),
            RuleVersion.V1));

        Assert.Equal("invalid_operation", result.ReasonCode);
    }

    private static PaymentSnapshot Snapshot(
        PaymentState state,
        AttemptOperation operation = AttemptOperation.Authorize,
        AttemptState? attemptState = null,
        RefundState? refundState = null)
    {
        var attempt = state is PaymentState.AwaitingMethod or PaymentState.Canceled
            ? null
            : new AttemptSnapshot(
                Attempt,
                operation,
                attemptState ?? AttemptState.Review,
                Hundred,
                operation == AttemptOperation.Void ? Attempt : null,
                null);
        var refund = refundState is null
            ? null
            : new RefundSnapshot(Refund, refundState.Value, Hundred);
        return new PaymentSnapshot(
            Payment,
            Merchant,
            state,
            Hundred,
            CapturedMinor: 100,
            Attempt: attempt,
            Refund: refund);
    }

    private static IEnumerable<(string TransactionKind, string AccountType, char Direction, long Minor)> Entries(
        IReadOnlyList<LedgerLine> ledger) =>
        ledger.Select(line => (line.TransactionKind, line.AccountType, line.Direction, line.Amount.Minor));
}
