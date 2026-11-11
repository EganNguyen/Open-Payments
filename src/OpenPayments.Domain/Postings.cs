using System.Security.Cryptography;
using System.Text;

namespace OpenPayments.Domain;

public sealed record LedgerLine(
    string TransactionKind,
    Guid TransactionId,
    short EntryNo,
    string AccountType,
    char Direction,
    Money Amount,
    Guid SubjectId,
    string ClassOfMoney);

public sealed record AttemptChange(
    AttemptId Id,
    AttemptOperation Operation,
    AttemptState State,
    string? FailureCode = null,
    AttemptId? VoidTargetAttemptId = null);

public sealed record RefundChange(RefundId Id, RefundState State, string? FailureCode = null);

public sealed record OutboxEvent(
    Guid EventId,
    string EventType,
    long Sequence,
    int SchemaVersion,
    Guid AggregateId);

public sealed record RuleVersion(int AttemptCap = 10)
{
    public static RuleVersion V1 { get; } = new();
}

public abstract record TransitionResult
{
    public sealed record Accepted(
        PaymentState NewState,
        AttemptChange? Attempt,
        RefundChange? Refund,
        IReadOnlyList<LedgerLine> Ledger,
        IReadOnlyList<OutboxEvent> Events) : TransitionResult;

    public sealed record Rejected(string ReasonCode) : TransitionResult;

    public sealed record NoOp(string ReasonCode) : TransitionResult;
}

/// <summary>
/// Deterministic ledger and event identity (D32). The same base id and posting kind always
/// produce the same id, so a repeated decision produces the same rows.
/// </summary>
public static class Posting
{
    public const string AuthHold = "auth_hold";
    public const string AuthHoldContra = "auth_hold_contra";
    public const string PspReceivable = "psp_receivable";
    public const string MerchantPayable = "merchant_payable";
    public const string RefundReserved = "refund_reserved";
    public const string PspRefundPayable = "psp_refund_payable";
    public const string SettlementBank = "settlement_bank";

    public static Guid Id(Guid baseId, string posting)
    {
        var bytes = SHA256.HashData(
            Encoding.UTF8.GetBytes(baseId.ToString("N") + ":" + posting));
        return new Guid(bytes.AsSpan(0, 16));
    }

    public static IReadOnlyList<LedgerLine> AuthorizationHold(AttemptId attempt, Money amount) =>
        Transaction(
            "authorization",
            Id(attempt.Value, "authorization"),
            attempt.Value,
            amount,
            (AuthHold, 'D', "logical"),
            (AuthHoldContra, 'C', "logical"));

    public static IReadOnlyList<LedgerLine> HoldRelease(AttemptId attempt, Money amount, string kind) =>
        Transaction(
            kind,
            Id(attempt.Value, kind + ":hold_release"),
            attempt.Value,
            amount,
            (AuthHoldContra, 'D', "logical"),
            (AuthHold, 'C', "logical"));

    public static IReadOnlyList<LedgerLine> CaptureMoney(AttemptId capture, Money amount) =>
        Transaction(
            "capture",
            Id(capture.Value, "capture:money"),
            capture.Value,
            amount,
            (PspReceivable, 'D', "real"),
            (MerchantPayable, 'C', "real"));

    public static IReadOnlyList<LedgerLine> RefundReservation(RefundId refund, Money amount) =>
        Transaction(
            "refund_reservation",
            Id(refund.Value, "refund_reservation"),
            refund.Value,
            amount,
            (MerchantPayable, 'D', "real"),
            (RefundReserved, 'C', "real"));

    public static IReadOnlyList<LedgerLine> RefundConfirmation(RefundId refund, Money amount) =>
        Transaction(
            "refund",
            Id(refund.Value, "refund"),
            refund.Value,
            amount,
            (RefundReserved, 'D', "real"),
            (PspRefundPayable, 'C', "real"));

    public static IReadOnlyList<LedgerLine> RefundRelease(RefundId refund, Money amount) =>
        Transaction(
            "refund_release",
            Id(refund.Value, "refund_release"),
            refund.Value,
            amount,
            (RefundReserved, 'D', "real"),
            (MerchantPayable, 'C', "real"));

    /// <summary>D21: the PSP pays the merchant; this clears the receivable the capture posted.</summary>
    public static IReadOnlyList<LedgerLine> SettlementOfCapture(
        Guid transactionId, AttemptId attempt, Money amount) =>
        Transaction(
            "settlement",
            transactionId,
            attempt.Value,
            amount,
            (SettlementBank, 'D', "real"),
            (PspReceivable, 'C', "real"));

    /// <summary>D21: a settled refund clears the payable the refund confirmation posted.</summary>
    public static IReadOnlyList<LedgerLine> SettlementOfRefund(
        Guid transactionId, RefundId refund, Money amount) =>
        Transaction(
            "settlement",
            transactionId,
            refund.Value,
            amount,
            (PspRefundPayable, 'D', "real"),
            (SettlementBank, 'C', "real"));

    public static OutboxEvent Event(Guid baseId, string eventType, long sequence, Guid aggregateId) =>
        new(Id(baseId, "event:" + eventType), eventType, sequence, 1, aggregateId);

    private static IReadOnlyList<LedgerLine> Transaction(
        string kind,
        Guid transactionId,
        Guid subjectId,
        Money amount,
        params (string AccountType, char Direction, string ClassOfMoney)[] entries)
    {
        var lines = new List<LedgerLine>(entries.Length);
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            lines.Add(new LedgerLine(
                kind,
                transactionId,
                (short)(index + 1),
                entry.AccountType,
                entry.Direction,
                amount,
                subjectId,
                entry.ClassOfMoney));
        }

        return lines;
    }
}
