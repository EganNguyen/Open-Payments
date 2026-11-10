using OpenPayments.Domain;

namespace OpenPayments.Psp.Resilience;

/// <summary>
/// Wraps one account's adapter with its token bucket, its breakers and the 429 backoff (C12,
/// C13, C51). A credential rejection is the one post-call failure that is known before the PSP
/// acted, so it becomes a stable rejection instead of an unknown outcome.
/// </summary>
public sealed class ResilientPspAdapter(
    IPspAdapter inner,
    AccountGate gate,
    PspAccount account,
    TimeSpan? maxRetryDelay = null) : IPspAdapter
{
    private readonly TimeSpan _maxRetryDelay = maxRetryDelay ?? TimeSpan.FromSeconds(2);

    public PspCapabilities Capabilities => inner.Capabilities;

    public Task<PspResult> AuthorizeAsync(AttemptId attempt, Money amount, PaymentMethodRef method, CancellationToken ct) =>
        RunAsync(Lane.Live, () => inner.AuthorizeAsync(attempt, amount, method, ct), ct);

    public Task<PspResult> SaleAsync(AttemptId attempt, Money amount, PaymentMethodRef method, CancellationToken ct) =>
        RunAsync(Lane.Live, () => inner.SaleAsync(attempt, amount, method, ct), ct);

    public Task<PspResult> CompleteActionAsync(AttemptId attempt, CancellationToken ct) =>
        RunAsync(Lane.Live, () => inner.CompleteActionAsync(attempt, ct), ct);

    public Task<PspResult> CaptureAsync(AttemptId attempt, Money amount, CancellationToken ct) =>
        RunAsync(Lane.Live, () => inner.CaptureAsync(attempt, amount, ct), ct);

    public Task<PspResult> VoidAsync(AttemptId attempt, AttemptId authorized, CancellationToken ct) =>
        RunAsync(Lane.Live, () => inner.VoidAsync(attempt, authorized, ct), ct);

    public Task<PspResult> RefundAsync(RefundId refund, Money amount, CancellationToken ct) =>
        RunAsync(Lane.Live, () => inner.RefundAsync(refund, amount, ct), ct);

    public Task<PspInquiry> RecoverAsync(AttemptId attempt, CancellationToken ct) =>
        RunAsync(Lane.Recovery, () => inner.RecoverAsync(attempt, ct), ct);

    public Task<PspInquiry> RecoverRefundAsync(RefundId refund, CancellationToken ct) =>
        RunAsync(Lane.Recovery, () => inner.RecoverRefundAsync(refund, ct), ct);

    public Task<PspInquiry> LookupByReferenceAsync(string pspReference, CancellationToken ct) =>
        RunAsync(Lane.Recovery, () => inner.LookupByReferenceAsync(pspReference, ct), ct);

    public Task<PspInquiry> CancelForProofAsync(AttemptId attempt, string? pspReference, CancellationToken ct) =>
        RunAsync(Lane.Recovery, () => inner.CancelForProofAsync(attempt, pspReference, ct), ct);

    private async Task<T> RunAsync<T>(Lane lane, Func<Task<T>> call, CancellationToken ct)
    {
        using var _ = gate.Acquire(
            account.Id, account.Psp, lane, account.RateLimitPerSecond, account.RecoveryShare);
        try
        {
            return await CallAsync(call, ct);
        }
#pragma warning disable CA1031 // The wrapper translates the PSP's failures; it never swallows them.
        catch (PspUnauthorizedException)
#pragma warning restore CA1031
        {
            gate.CredentialRejected(account.Id, account.Psp);
            throw new PspRejectedException("psp_credential_rejected");
        }
        catch (PspDnsException)
        {
            gate.Rejected("dns_failure");
            gate.Failure(account.Id, account.Psp);
            throw;
        }
        catch (PspUnavailableException)
        {
            gate.Failure(account.Id, account.Psp);
            throw;
        }
    }

    private async Task<T> CallAsync<T>(Func<Task<T>> call, CancellationToken ct)
    {
        try
        {
            var result = await call();
            gate.Success(account.Id, account.Psp);
            return result;
        }
        catch (PspRateLimitException rateLimited)
        {
            // C12: honour Retry-After, once, under the same key. No breaker opens for a 429.
            gate.Rejected("psp_429");
            var delay = rateLimited.RetryAfter > _maxRetryDelay ? _maxRetryDelay : rateLimited.RetryAfter;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, ct);
            }

            var retried = await call();
            gate.Success(account.Id, account.Psp);
            return retried;
        }
    }
}

public sealed class ResilientPspAdapterFactory(IPspAdapterFactory inner, AccountGate gate) : IPspAdapterFactory
{
    public PspCapabilities Capabilities(PspAccount account) => inner.Capabilities(account);

    public IPspAdapter For(PspAccount account) =>
        new ResilientPspAdapter(inner.For(account), gate, account);
}
