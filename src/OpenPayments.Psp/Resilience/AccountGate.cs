using System.Collections.Concurrent;

namespace OpenPayments.Psp.Resilience;

public enum Lane
{
    Live,
    Recovery,
}

/// <summary>
/// Per-account token buckets and breakers (D34, C51). The account's rate limit is split into a
/// live lane and a recovery lane; a recovery burst can't take the live share, and one account's
/// revoked credential opens only its own breaker.
/// </summary>
public sealed class AccountGate(TimeProvider? clock = null)
{
    private readonly ConcurrentDictionary<(Guid Account, Lane Lane), TokenBucket> _buckets = new();
    private readonly ConcurrentDictionary<Guid, Breaker> _accountBreakers = new();
    private readonly ConcurrentDictionary<string, Breaker> _pspBreakers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _rejected = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public IDisposable Acquire(Guid account, string psp, Lane lane, int accountLimitPerSecond, decimal recoveryShare)
    {
        if (AccountOpen(account) || PspOpen(psp))
        {
            Rejected("breaker_open");
            throw new PspRejectedException("breaker_open");
        }

        var limit = lane == Lane.Live
            ? accountLimitPerSecond * (1 - (double)recoveryShare)
            : accountLimitPerSecond * (double)recoveryShare;
        var bucket = _buckets.GetOrAdd((account, lane), _ => new TokenBucket(Math.Max(1, limit), _clock));
        if (!bucket.TryTake())
        {
            Rejected("rate_limited");
            throw new PspRejectedException("rate_limited");
        }

        return new Lease();
    }

    public void Success(Guid account, string psp)
    {
        Account(account).Success();
        Psp(psp).Success();
    }

    public void Failure(Guid account, string psp)
    {
        Account(account).Failure();
        Psp(psp).Failure();
    }

    public void CredentialRejected(Guid account, string psp)
    {
        // C51: only this account's breaker opens; the PSP's and other accounts keep working.
        Account(account).OpenNow();
    }

    public void Rejected(string reason) =>
        _rejected.AddOrUpdate(reason, 1, static (_, count) => count + 1);

    public long RejectedCount(string reason) => _rejected.TryGetValue(reason, out var count) ? count : 0;

    public bool AccountOpen(Guid account) => Account(account).IsOpen;

    public bool PspOpen(string psp) => Psp(psp).IsOpen;

    private Breaker Account(Guid account) =>
        _accountBreakers.GetOrAdd(account, _ => new Breaker(threshold: 5, openFor: TimeSpan.FromSeconds(30), _clock));

    private Breaker Psp(string psp) =>
        _pspBreakers.GetOrAdd(psp, _ => new Breaker(threshold: 20, openFor: TimeSpan.FromSeconds(30), _clock));

    private sealed class Lease : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private sealed class TokenBucket
    {
        private readonly Lock _lock = new();
        private readonly double _rate;
        private readonly TimeProvider _clock;
        private double _tokens;
        private long _last;

        public TokenBucket(double ratePerSecond, TimeProvider clock)
        {
            _rate = ratePerSecond;
            _clock = clock;
            _tokens = ratePerSecond;
            _last = clock.GetTimestamp();
        }

        public bool TryTake()
        {
            lock (_lock)
            {
                var now = _clock.GetTimestamp();
                var elapsed = (now - _last) / (double)_clock.TimestampFrequency;
                _last = now;
                if (elapsed > 0)
                {
                    _tokens = Math.Min(_rate, _tokens + (elapsed * _rate));
                }

                if (_tokens < 1)
                {
                    return false;
                }

                _tokens -= 1;
                return true;
            }
        }
    }
}
