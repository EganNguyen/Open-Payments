using OpenPayments.Domain;

namespace OpenPayments.Psp;

public sealed record PspAccount(
    Guid Id,
    MerchantId MerchantId,
    string Psp,
    string Mode,
    string CredentialRef,
    int RateLimitPerSecond = 1000,
    decimal RecoveryShare = 0.10m);

public sealed record PaymentMethodRef(string Type, string Ref, string? Details = null);

public enum RecoveryMode
{
    Replay,
    AuthoritativeLookup,
    TechnicalCancel,
}

public enum ActionProof
{
    None,
    ConfirmedCancel,
}

/// <summary>
/// What one adapter can do (D60). Recovery is declared per situation, not one mode: how to
/// recover an operation the PSP never answered, whether a lookup by the PSP's own reference is
/// consistent, and how to prove an abandoned action can't complete.
/// </summary>
public sealed class PspCapabilities
{
    public PspCapabilities(
        bool SupportsSale,
        RecoveryMode Unanswered,
        bool ConsistentLookupByReference,
        ActionProof ActionProof,
        TimeSpan KeyRetention,
        TimeSpan ReplayWindow,
        TimeSpan AuthorizationWindow,
        TimeSpan SettlementLag,
        TimeSpan LookupQuietWindow)
    {
        if (ReplayWindow > KeyRetention / 4)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ReplayWindow), "The replay window must be at most a quarter of key retention.");
        }

        if (ReplayWindow <= TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(
                nameof(ReplayWindow), "The replay window must exceed the attempt timeout.");
        }

        this.SupportsSale = SupportsSale;
        this.Unanswered = Unanswered;
        this.ConsistentLookupByReference = ConsistentLookupByReference;
        this.ActionProof = ActionProof;
        this.KeyRetention = KeyRetention;
        this.ReplayWindow = ReplayWindow;
        this.AuthorizationWindow = AuthorizationWindow;
        this.SettlementLag = SettlementLag;
        this.LookupQuietWindow = LookupQuietWindow;
    }

    public bool SupportsSale { get; }

    public RecoveryMode Unanswered { get; }

    public bool ConsistentLookupByReference { get; }

    public ActionProof ActionProof { get; }

    public TimeSpan KeyRetention { get; }

    public TimeSpan ReplayWindow { get; }

    public TimeSpan AuthorizationWindow { get; }

    public TimeSpan SettlementLag { get; }

    public TimeSpan LookupQuietWindow { get; }
}

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

/// <summary>The PSP answered 429; retry after the delay, under the same key.</summary>
public sealed class PspRateLimitException(TimeSpan retryAfter, string message = "The PSP rate limited the request.")
    : Exception(message)
{
    public TimeSpan RetryAfter { get; } = retryAfter;
}

/// <summary>Name resolution failed, so nothing was sent (C16).</summary>
public sealed class PspDnsException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The PSP rejected this account's credential (401/403), before it acted.</summary>
public sealed class PspUnauthorizedException(string message = "The PSP rejected the credential.")
    : Exception(message);

/// <summary>A request the local gate refused before it reached the PSP.</summary>
public sealed class PspRejectedException(string reason) : Exception(reason)
{
    public string Reason { get; } = reason;
}

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

    Task<PspInquiry> LookupByReferenceAsync(string pspReference, CancellationToken ct);

    Task<PspInquiry> CancelForProofAsync(AttemptId attempt, string? pspReference, CancellationToken ct);
}

public interface IPspAdapterFactory
{
    PspCapabilities Capabilities(PspAccount account);

    IPspAdapter For(PspAccount account);
}
