using OpenPayments.Domain;
using OpenPayments.Psp;
using OpenPayments.Psp.Resilience;
using Xunit;

namespace OpenPayments.MockPsp.Tests;

// Task 23: the capability contract, the per-account gate and the wrapper's 429/401 behaviour.
public sealed class ResilienceTests
{
    [Fact]
    public void U5_capabilities_reject_replay_window_beyond_quarter_retention()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Capabilities(replayWindow: TimeSpan.FromMinutes(31)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Capabilities(replayWindow: TimeSpan.FromSeconds(20)));
        _ = Capabilities(replayWindow: TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void Recovery_lane_cannot_take_live_lane_tokens()
    {
        var gate = new AccountGate();
        var account = Guid.NewGuid();
        using var live = gate.Acquire(account, "mock", Lane.Live, 10, 0.10m);
        using var recovery = gate.Acquire(account, "mock", Lane.Recovery, 10, 0.10m);

        // The recovery bucket is 10% of the account: one token a second, so a second take fails
        // while the live lane's nine tokens are untouched.
        var rejected = Assert.Throws<PspRejectedException>(
            () => gate.Acquire(account, "mock", Lane.Recovery, 10, 0.10m));
        Assert.Equal("rate_limited", rejected.Reason);
        using var liveAgain = gate.Acquire(account, "mock", Lane.Live, 10, 0.10m);
    }

    [Fact]
    public void C51_revoked_credential_opens_only_that_accounts_breaker()
    {
        var gate = new AccountGate();
        var revoked = Guid.NewGuid();
        var healthy = Guid.NewGuid();
        gate.CredentialRejected(revoked, "mock");

        Assert.True(gate.AccountOpen(revoked));
        Assert.False(gate.AccountOpen(healthy));
        Assert.False(gate.PspOpen("mock"));
        using var lease = gate.Acquire(healthy, "mock", Lane.Live, 100, 0.10m);
    }

    [Fact]
    public async Task C12_psp_429_backs_off_once_with_the_same_key_and_no_breaker()
    {
        var gate = new AccountGate();
        var fake = new FakeAdapter { RateLimitOnce = true };
        var adapter = new ResilientPspAdapter(
            fake, gate, new PspAccount(Guid.NewGuid(), new MerchantId(Guid.NewGuid()), "mock", "test", "ref"),
            maxRetryDelay: TimeSpan.Zero);

        var result = await adapter.AuthorizeAsync(
            AttemptId.New(), new Money(100, CurrencyCode.Usd), new PaymentMethodRef("card", "tok"), default);

        Assert.IsType<PspResult.Approved>(result);
        Assert.Equal(2, fake.Calls);
        Assert.False(gate.AccountOpen(fake.Account));
        Assert.Equal(1, gate.RejectedCount("psp_429"));
    }

    [Fact]
    public async Task C51_credential_rejection_maps_to_a_stable_code_and_opens_the_account_breaker()
    {
        var gate = new AccountGate();
        var fake = new FakeAdapter { Unauthorized = true };
        var account = new PspAccount(
            Guid.NewGuid(), new MerchantId(Guid.NewGuid()), "mock", "test", "ref");
        var adapter = new ResilientPspAdapter(fake, gate, account);

        var rejected = await Assert.ThrowsAsync<PspRejectedException>(() => adapter.AuthorizeAsync(
            AttemptId.New(), new Money(100, CurrencyCode.Usd), new PaymentMethodRef("card", "tok"), default));

        Assert.Equal("psp_credential_rejected", rejected.Reason);
        Assert.True(gate.AccountOpen(account.Id));
        Assert.Equal(1, gate.RejectedCount("psp_credential_rejected"));
    }

    private static PspCapabilities Capabilities(TimeSpan replayWindow) => new(
        SupportsSale: true,
        Unanswered: RecoveryMode.AuthoritativeLookup,
        ConsistentLookupByReference: true,
        ActionProof: ActionProof.ConfirmedCancel,
        KeyRetention: TimeSpan.FromHours(2),
        ReplayWindow: replayWindow,
        AuthorizationWindow: TimeSpan.FromDays(7),
        SettlementLag: TimeSpan.FromHours(1),
        LookupQuietWindow: TimeSpan.Zero);

    private sealed class FakeAdapter : IPspAdapter
    {
        public Guid Account { get; } = Guid.NewGuid();
        public int Calls { get; private set; }
        public bool RateLimitOnce { get; init; }
        public bool Unauthorized { get; init; }

        public PspCapabilities Capabilities => Capabilities(TimeSpan.FromMinutes(30));

        public Task<PspResult> AuthorizeAsync(
            AttemptId attempt, Money amount, PaymentMethodRef method, CancellationToken ct)
        {
            Calls++;
            if (Unauthorized)
            {
                throw new PspUnauthorizedException();
            }

            if (RateLimitOnce && Calls == 1)
            {
                throw new PspRateLimitException(TimeSpan.Zero);
            }

            return Task.FromResult<PspResult>(new PspResult.Approved("psp_ok"));
        }

        public Task<PspResult> SaleAsync(AttemptId a, Money m, PaymentMethodRef p, CancellationToken ct) => throw new NotSupportedException();
        public Task<PspResult> CompleteActionAsync(AttemptId a, CancellationToken ct) => throw new NotSupportedException();
        public Task<PspResult> CaptureAsync(AttemptId a, Money m, CancellationToken ct) => throw new NotSupportedException();
        public Task<PspResult> VoidAsync(AttemptId a, AttemptId b, CancellationToken ct) => throw new NotSupportedException();
        public Task<PspResult> RefundAsync(RefundId r, Money m, CancellationToken ct) => throw new NotSupportedException();
        public Task<PspInquiry> RecoverAsync(AttemptId a, CancellationToken ct) => throw new NotSupportedException();
        public Task<PspInquiry> RecoverRefundAsync(RefundId r, CancellationToken ct) => throw new NotSupportedException();
        public Task<PspInquiry> LookupByReferenceAsync(string p, CancellationToken ct) => throw new NotSupportedException();
        public Task<PspInquiry> CancelForProofAsync(AttemptId a, string? p, CancellationToken ct) => throw new NotSupportedException();
    }
}
