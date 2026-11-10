namespace OpenPayments.Domain;

public enum PaymentState
{
    AwaitingMethod,
    PspPending,
    ActionRequired,
    Unknown,
    Review,
    Authorized,
    Captured,
    PartiallyRefunded,
    Refunded,
    Canceled,
    Voided,
    Expired,
}

public enum AttemptState
{
    Pending,
    ActionRequired,
    Unknown,
    Succeeded,
    Failed,
    Review,
}

public enum AttemptOperation
{
    Authorize,
    Sale,
    Capture,
    Void,
}

public enum RefundState
{
    Pending,
    Unknown,
    Succeeded,
    Failed,
    Review,
}

public enum PspAnswerOutcome
{
    Approved,
    Declined,
    RequiresAction,
}

public enum RecoveryOutcome
{
    Approved,
    Declined,
    RequiresAction,
    ProvenNotCharged,
    Inconclusive,
}

public enum PaymentResolution
{
    Authorize,
    AwaitingMethod,
    Capture,
}

public enum RefundResolution
{
    Confirm,
    Release,
}

public sealed record AttemptSnapshot(
    AttemptId Id,
    AttemptOperation Operation,
    AttemptState State,
    Money Amount,
    AttemptId? TargetAttemptId = null,
    string? FailureCode = null);

public sealed record RefundSnapshot(
    RefundId Id,
    RefundState State,
    Money Amount,
    string? FailureCode = null);

public sealed record PaymentSnapshot(
    PaymentId Id,
    MerchantId MerchantId,
    PaymentState State,
    Money Amount,
    long CapturedMinor = 0,
    long RefundedMinor = 0,
    long RefundPendingMinor = 0,
    int AttemptCount = 0,
    int EventSeq = 0,
    AttemptSnapshot? Attempt = null,
    RefundSnapshot? Refund = null,
    bool LiveCapture = false,
    bool LiveVoid = false,
    DateTimeOffset? AuthorizedUntil = null,
    DateTimeOffset? ExpiresAt = null,
    bool HoldPosted = false)
{
    public long RefundableMinor => CapturedMinor - RefundedMinor - RefundPendingMinor;

    public bool IsFinal => State is PaymentState.Refunded
        or PaymentState.Canceled
        or PaymentState.Voided
        or PaymentState.Expired;
}
