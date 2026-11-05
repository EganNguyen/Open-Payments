namespace OpenPayments.Domain;

/// <summary>
/// The pure inputs to <see cref="Decide"/>. Every identifier and time is supplied by the
/// caller; the function has no clock, random source or I/O.
/// </summary>
public abstract record PaymentCommand(DateTimeOffset OccurredAt)
{
    public sealed record CreatePayment(
        PaymentId PaymentId,
        MerchantId MerchantId,
        Money Amount,
        bool HasMethod,
        AttemptOperation Operation,
        DateTimeOffset OccurredAt,
        DateTimeOffset? ExpiresAt = null,
        AttemptId? AttemptId = null) : PaymentCommand(OccurredAt);

    public sealed record Attempt(AttemptId AttemptId, DateTimeOffset OccurredAt) : PaymentCommand(OccurredAt);

    public sealed record Sale(AttemptId AttemptId, DateTimeOffset OccurredAt) : PaymentCommand(OccurredAt);

    public sealed record FraudBlock(AttemptId AttemptId, string FailureCode, DateTimeOffset OccurredAt)
        : PaymentCommand(OccurredAt);

    public sealed record PspAnswer(
        AttemptId AttemptId,
        AttemptOperation Operation,
        PspAnswerOutcome Outcome,
        Money? AnswerAmount,
        string? FailureCode,
        string? ActionType,
        DateTimeOffset OccurredAt) : PaymentCommand(OccurredAt);

    public sealed record RecoveryAnswer(
        AttemptId AttemptId,
        AttemptOperation Operation,
        RecoveryOutcome Outcome,
        Money? AnswerAmount,
        string? FailureCode,
        string? ActionType,
        DateTimeOffset OccurredAt) : PaymentCommand(OccurredAt);

    public sealed record Timeout(AttemptId AttemptId, AttemptOperation Operation, DateTimeOffset OccurredAt)
        : PaymentCommand(OccurredAt);

    public sealed record ActionCompleted(AttemptId AttemptId, DateTimeOffset OccurredAt) : PaymentCommand(OccurredAt);

    public sealed record Capture(AttemptId CaptureAttemptId, Money Amount, DateTimeOffset OccurredAt)
        : PaymentCommand(OccurredAt);

    public sealed record CaptureAnswer(
        AttemptId CaptureAttemptId,
        bool Approved,
        Money? AnswerAmount,
        string? FailureCode,
        bool TimedOut,
        DateTimeOffset OccurredAt) : PaymentCommand(OccurredAt);

    public sealed record RefundReserve(RefundId RefundId, Money Amount, DateTimeOffset OccurredAt)
        : PaymentCommand(OccurredAt);

    public sealed record RefundConfirm(RefundId RefundId, Money? AnswerAmount, DateTimeOffset OccurredAt)
        : PaymentCommand(OccurredAt);

    public sealed record RefundRelease(
        RefundId RefundId,
        bool ProvenNotCharged,
        string? FailureCode,
        DateTimeOffset OccurredAt) : PaymentCommand(OccurredAt);

    public sealed record RefundTimeout(RefundId RefundId, DateTimeOffset OccurredAt) : PaymentCommand(OccurredAt);

    public sealed record RefundMismatch(RefundId RefundId, Money? AnswerAmount, DateTimeOffset OccurredAt)
        : PaymentCommand(OccurredAt);

    public sealed record Void(AttemptId VoidAttemptId, AttemptId TargetAttemptId, DateTimeOffset OccurredAt)
        : PaymentCommand(OccurredAt);

    public sealed record VoidAnswer(
        AttemptId VoidAttemptId,
        bool Approved,
        bool TimedOut,
        string? FailureCode,
        DateTimeOffset OccurredAt) : PaymentCommand(OccurredAt);

    public sealed record Expiry(DateTimeOffset OccurredAt) : PaymentCommand(OccurredAt);

    public sealed record Cancel(DateTimeOffset OccurredAt) : PaymentCommand(OccurredAt);

    public sealed record ManualResolution(
        PaymentResolution Resolution,
        Money? Amount,
        DateTimeOffset OccurredAt) : PaymentCommand(OccurredAt);

    public sealed record LateApproval(
        AttemptId AttemptId,
        AttemptOperation Operation,
        Money? AnswerAmount,
        string? FailureCode,
        DateTimeOffset OccurredAt) : PaymentCommand(OccurredAt);
}
