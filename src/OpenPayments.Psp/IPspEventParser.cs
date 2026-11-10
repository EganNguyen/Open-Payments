using OpenPayments.Domain;

namespace OpenPayments.Psp;

public enum PspEventKind
{
    Authorized,
    Captured,
    SaleCaptured,
    Declined,
    ActionRequired,
    Voided,
    VoidFailed,
    Refunded,
    RefundFailed,
    Canceled,
}

/// <summary>Which of our operations a PSP event names: an attempt or a refund.</summary>
public sealed record PspEventReference(AttemptId? Attempt, RefundId? Refund)
{
    public static PspEventReference ForAttempt(AttemptId id) => new(id, null);

    public static PspEventReference ForRefund(RefundId id) => new(null, id);
}

public sealed record PspEvent(
    string EventId,
    PspEventKind Kind,
    PspEventReference Reference,
    string? PspReference,
    Money? Amount,
    string? FailureCode,
    string? ActionType);

/// <summary>Verifies and parses one PSP callback (D30, D59). It writes nothing.</summary>
public interface IPspEventParser
{
    Result<PspEvent> Parse(
        ReadOnlySpan<byte> rawBody,
        IReadOnlyDictionary<string, string> headers,
        string signingSecret,
        DateTimeOffset now);
}
