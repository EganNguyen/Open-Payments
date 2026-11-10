using System.Collections.Concurrent;

namespace OpenPayments.MockPsp;

public sealed class MockAttempt
{
    private readonly object _effectGate = new();
    private Task? _effectTask;
    private int _effectWritten;
    private int _cancelled;

    public required string Key { get; init; }

    public required string Op { get; init; }

    public required string Account { get; init; }

    public required long AmountMinor { get; init; }

    public required string Currency { get; init; }

    public required string Outcome { get; init; }

    public string? FailureCode { get; init; }

    public ActionPayload? Action { get; init; }

    public required string PspReference { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset LandingAt { get; init; }

    public required DateTimeOffset VisibleAt { get; init; }

    public required bool Hang { get; init; }

    public bool Cancelled => Volatile.Read(ref _cancelled) != 0;

    public int EffectWritten => Volatile.Read(ref _effectWritten);

    public void MarkCancelled() => Interlocked.Exchange(ref _cancelled, 1);

    public Task EnsureEffectAsync(Func<Task> write)
    {
        lock (_effectGate)
        {
            return _effectTask ??= WriteEffectAsync(write);
        }
    }

    private async Task WriteEffectAsync(Func<Task> write)
    {
        var delay = LandingAt - DateTimeOffset.UtcNow;
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay);
        }

        if (Cancelled)
        {
            return;
        }

        await write();
        Interlocked.Exchange(ref _effectWritten, 1);
    }
}

public sealed record MockInquiry(string Status, MockAttempt? Attempt, string? FailureCode);

public enum CancelResult
{
    Cancelled,
    NotFound,
    AlreadyLanded,
    NotCancellable,
}

public sealed class MockAttemptStore
{
    private readonly ConcurrentDictionary<string, MockAttempt> _attempts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, MockAccountState> _accounts = new(StringComparer.Ordinal);
    private readonly EffectLog _effectLog;
    private readonly MockConfigStore _config;
    private long _requests;
    private long _approved;
    private long _declined;
    private long _actionRequired;
    private long _errors;
    private long _rateLimited;
    private long _revoked;
    private long _evicted;
    private long _effects;

    public MockAttemptStore(EffectLog effectLog, MockConfigStore config)
    {
        _effectLog = effectLog;
        _config = config;
    }

    public MockAccountState Account(string account) =>
        _accounts.GetOrAdd(account, _ => new MockAccountState());

    public MockConfig Config => _config.Current;

    public bool IsRevoked(string account) => Account(account).Revoked;

    public void RecordRevoked() => Interlocked.Increment(ref _revoked);

    public bool TryRateLimit(string account)
    {
        var config = _config.Current;
        if (Account(account).TryAcquire(config.RateLimitPerS, DateTimeOffset.UtcNow))
        {
            return true;
        }

        Interlocked.Increment(ref _rateLimited);
        return false;
    }

    public MockAttempt GetOrCreate(
        string key,
        string op,
        string account,
        long amountMinor,
        string currency,
        DateTimeOffset now)
    {
        while (true)
        {
            if (_attempts.TryGetValue(key, out var existing))
            {
                if (!IsExpired(existing, now))
                {
                    Interlocked.Increment(ref _requests);
                    return existing;
                }

                _attempts.TryRemove(new KeyValuePair<string, MockAttempt>(key, existing));
                Interlocked.Increment(ref _evicted);
                continue;
            }

            var config = _config.Current;
            var decision = Outcomes.Decide(key, config);
            var mismatch = Outcomes.Hit(key, config.Seed, "mismatch", config.MismatchRate);
            var effectiveMinor = mismatch && amountMinor < long.MaxValue ? amountMinor + 1 : amountMinor;
            var lateLanding = Outcomes.Hit(key, config.Seed, "late", config.LateLandingRate);
            var visibleAt = now.AddSeconds(config.LookupLagS);
            var landingAt = Outcomes.IsApproved(decision.Outcome) && lateLanding
                ? visibleAt
                : now;

            var attempt = new MockAttempt
            {
                Key = key,
                Op = op,
                Account = account,
                AmountMinor = effectiveMinor,
                Currency = currency,
                Outcome = decision.Outcome,
                FailureCode = decision.FailureCode,
                Action = decision.Action,
                PspReference = Outcomes.Reference(key, config.Seed),
                CreatedAt = now,
                LandingAt = landingAt,
                VisibleAt = visibleAt,
                Hang = decision.Hang,
            };

            if (string.Equals(decision.Outcome, "error", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _errors);
                return attempt;
            }

            if (_attempts.TryAdd(key, attempt))
            {
                Interlocked.Increment(ref _requests);
                switch (decision.Outcome)
                {
                    case "approved":
                        Interlocked.Increment(ref _approved);
                        break;
                    case "declined":
                        Interlocked.Increment(ref _declined);
                        break;
                    case "action_required":
                        Interlocked.Increment(ref _actionRequired);
                        break;
                }

                return attempt;
            }
        }
    }

    public MockInquiry Inquire(string key, DateTimeOffset now)
    {
        if (!_attempts.TryGetValue(key, out var attempt))
        {
            return new MockInquiry("unknown_attempt", null, null);
        }

        if (IsExpired(attempt, now))
        {
            Interlocked.Increment(ref _evicted);
            return new MockInquiry("evicted", attempt, null);
        }

        if (attempt.Cancelled)
        {
            return new MockInquiry("declined", attempt, "cancelled");
        }

        if (now < attempt.VisibleAt)
        {
            return new MockInquiry("unknown_attempt", attempt, null);
        }

        return new MockInquiry(attempt.Outcome, attempt, attempt.FailureCode);
    }

    public CancelResult Cancel(string key, DateTimeOffset now)
    {
        if (!_attempts.TryGetValue(key, out var attempt))
        {
            return CancelResult.NotFound;
        }

        if (IsExpired(attempt, now))
        {
            return CancelResult.NotFound;
        }

        if (attempt.Cancelled)
        {
            return CancelResult.Cancelled;
        }

        if (attempt.EffectWritten != 0)
        {
            return CancelResult.AlreadyLanded;
        }

        if (!Outcomes.IsApproved(attempt.Outcome))
        {
            return CancelResult.NotCancellable;
        }

        attempt.MarkCancelled();
        return CancelResult.Cancelled;
    }

    public MockAttempt? FindByReference(string reference)
    {
        foreach (var attempt in _attempts.Values)
        {
            if (string.Equals(attempt.PspReference, reference, StringComparison.Ordinal))
            {
                return attempt;
            }
        }

        return null;
    }

    public MockAttempt? LastApprovedAuthorize(string account) =>
        _attempts.Values
            .Where(a => string.Equals(a.Account, account, StringComparison.Ordinal)
                && a.Op is "authorize" or "sale"
                && Outcomes.IsApproved(a.Outcome))
            .OrderByDescending(a => a.CreatedAt)
            .FirstOrDefault();

    public IReadOnlyList<MockAttempt> ForAccount(string account) =>
        [.. _attempts.Values.Where(a => string.Equals(a.Account, account, StringComparison.Ordinal))];

    public MockAttempt? FindByKey(string key) =>
        _attempts.TryGetValue(key, out var attempt) ? attempt : null;

    public Task? TryStartEffect(MockAttempt attempt)
    {
        if (!Outcomes.IsApproved(attempt.Outcome) || attempt.Cancelled)
        {
            return null;
        }

        return attempt.EnsureEffectAsync(async () =>
        {
            await _effectLog.AppendAsync(attempt.Key, attempt.Op, attempt.AmountMinor, attempt.Currency);
            Interlocked.Increment(ref _effects);
        });
    }

    public IReadOnlyDictionary<string, long> Stats() =>
        new Dictionary<string, long>
        {
            ["requests"] = Interlocked.Read(ref _requests),
            ["approved"] = Interlocked.Read(ref _approved),
            ["declined"] = Interlocked.Read(ref _declined),
            ["action_required"] = Interlocked.Read(ref _actionRequired),
            ["errors"] = Interlocked.Read(ref _errors),
            ["rate_limited"] = Interlocked.Read(ref _rateLimited),
            ["revoked"] = Interlocked.Read(ref _revoked),
            ["evicted"] = Interlocked.Read(ref _evicted),
            ["effects"] = Interlocked.Read(ref _effects),
        };

    private bool IsExpired(MockAttempt attempt, DateTimeOffset now)
    {
        var retention = Account(attempt.Account).KeyRetentionS ?? _config.Current.KeyRetentionS;
        return now - attempt.CreatedAt >= TimeSpan.FromSeconds(retention);
    }
}
