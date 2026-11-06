using OpenPayments.Domain;

namespace OpenPayments.Psp;

public sealed record PspAccount(
    Guid Id,
    MerchantId MerchantId,
    string Psp,
    string Mode,
    string CredentialRef);

public sealed record PaymentMethodRef(string Type, string Ref, string? Details = null);

public sealed record PspCapabilities(bool SupportsSale, string RecoveryMode, TimeSpan KeyRetention);

public abstract record PspResult
{
    public sealed record Approved(string PspReference, Money? Amount = null) : PspResult;

    public sealed record Declined(string FailureCode) : PspResult;

    public sealed record RequiresAction(string ActionType, string? Url, DateTimeOffset ExpiresAt) : PspResult;
}

public abstract record PspInquiry
{
    public sealed record Answered(PspResult Result) : PspInquiry;

    public sealed record ProvenNotCharged(string FailureCode) : PspInquiry;

    public sealed record Inconclusive(string Reason) : PspInquiry;
}

public sealed class PspTimeoutException(string message, Exception? inner = null)
    : Exception(message, inner);

public sealed class PspUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);

public interface IPspAdapter
{
    PspCapabilities Capabilities { get; }

    Task<PspResult> AuthorizeAsync(AttemptId attempt, Money amount, PaymentMethodRef method, CancellationToken ct);

    Task<PspResult> SaleAsync(AttemptId attempt, Money amount, PaymentMethodRef method, CancellationToken ct);

    Task<PspResult> CompleteActionAsync(AttemptId attempt, CancellationToken ct);

    Task<PspResult> CaptureAsync(AttemptId attempt, Money amount, CancellationToken ct);

    Task<PspResult> VoidAsync(AttemptId attempt, AttemptId authorized, CancellationToken ct);

    Task<PspResult> RefundAsync(RefundId refund, Money amount, CancellationToken ct);

    Task<PspInquiry> RecoverAsync(AttemptId attempt, CancellationToken ct);

    Task<PspInquiry> RecoverRefundAsync(RefundId refund, CancellationToken ct);
}

public interface IPspAdapterFactory
{
    PspCapabilities Capabilities(PspAccount account);

    IPspAdapter For(PspAccount account);
}
