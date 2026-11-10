using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace OpenPayments.MockPsp;

public sealed class MockConfig
{
    public int LatencyMs { get; set; }

    public int JitterMs { get; set; }

    public double DeclineRate { get; set; }

    public double ErrorRate { get; set; }

    public double ApproveThenHangRate { get; set; }

    public double LateLandingRate { get; set; }

    public int RateLimitPerS { get; set; } = 1000;

    public double ActionRate { get; set; }

    public double MismatchRate { get; set; }

    public string RecoveryMode { get; set; } = "authoritative_lookup";

    public double KeyRetentionS { get; set; } = 7200;

    public double LookupLagS { get; set; }

    public double CallbackRate { get; set; }

    public double CallbackDuplicateRate { get; set; }

    public double CallbackRedeliverUntilS { get; set; } = 259200;

    public bool SupportsSale { get; set; } = true;

    public bool ConsistentLookup { get; set; } = true;

    public string ActionProof { get; set; } = "none";

    public double AuthorizationWindowS { get; set; } = 7 * 86400;

    public int CallbackDelayMs { get; set; }

    public string? CallbackUrl { get; set; }

    public int Seed { get; set; } = 42;

    public MockConfig Clone() => (MockConfig)MemberwiseClone();

    public void Apply(MockConfigUpdate update)
    {
        if (update.LatencyMs is { } latencyMs)
        {
            LatencyMs = Math.Max(0, latencyMs);
        }

        if (update.JitterMs is { } jitterMs)
        {
            JitterMs = Math.Max(0, jitterMs);
        }

        if (update.DeclineRate is { } declineRate)
        {
            DeclineRate = Math.Clamp(declineRate, 0, 1);
        }

        if (update.ErrorRate is { } errorRate)
        {
            ErrorRate = Math.Clamp(errorRate, 0, 1);
        }

        if (update.ApproveThenHangRate is { } hangRate)
        {
            ApproveThenHangRate = Math.Clamp(hangRate, 0, 1);
        }

        if (update.LateLandingRate is { } lateLandingRate)
        {
            LateLandingRate = Math.Clamp(lateLandingRate, 0, 1);
        }

        if (update.RateLimitPerS is { } rateLimitPerS)
        {
            RateLimitPerS = Math.Max(1, rateLimitPerS);
        }

        if (update.ActionRate is { } actionRate)
        {
            ActionRate = Math.Clamp(actionRate, 0, 1);
        }

        if (update.MismatchRate is { } mismatchRate)
        {
            MismatchRate = Math.Clamp(mismatchRate, 0, 1);
        }

        if (update.RecoveryMode is { Length: > 0 } recoveryMode)
        {
            RecoveryMode = recoveryMode;
        }

        if (update.KeyRetentionS is { } keyRetentionS)
        {
            KeyRetentionS = Math.Max(0, keyRetentionS);
        }

        if (update.LookupLagS is { } lookupLagS)
        {
            LookupLagS = Math.Max(0, lookupLagS);
        }

        if (update.CallbackRate is { } callbackRate)
        {
            CallbackRate = Math.Clamp(callbackRate, 0, 1);
        }

        if (update.CallbackDuplicateRate is { } duplicateRate)
        {
            CallbackDuplicateRate = Math.Clamp(duplicateRate, 0, 1);
        }

        if (update.CallbackRedeliverUntilS is { } redeliver)
        {
            CallbackRedeliverUntilS = Math.Max(0, redeliver);
        }

        if (update.SupportsSale is { } supportsSale)
        {
            SupportsSale = supportsSale;
        }

        if (update.ConsistentLookup is { } consistentLookup)
        {
            ConsistentLookup = consistentLookup;
        }

        if (update.ActionProof is { Length: > 0 } actionProof)
        {
            ActionProof = actionProof;
        }

        if (update.AuthorizationWindowS is { } authorizationWindow)
        {
            AuthorizationWindowS = Math.Max(0, authorizationWindow);
        }

        if (update.CallbackDelayMs is { } callbackDelayMs)
        {
            CallbackDelayMs = Math.Max(0, callbackDelayMs);
        }

        if (update.CallbackUrl is { } callbackUrl)
        {
            CallbackUrl = callbackUrl.Length == 0 ? null : callbackUrl;
        }

        if (update.Seed is { } seed)
        {
            Seed = seed;
        }
    }
}

public sealed class MockConfigUpdate
{
    public int? LatencyMs { get; set; }

    public int? JitterMs { get; set; }

    public double? DeclineRate { get; set; }

    public double? ErrorRate { get; set; }

    public double? ApproveThenHangRate { get; set; }

    public double? LateLandingRate { get; set; }

    public int? RateLimitPerS { get; set; }

    public double? ActionRate { get; set; }

    public double? MismatchRate { get; set; }

    public string? RecoveryMode { get; set; }

    public double? KeyRetentionS { get; set; }

    public double? LookupLagS { get; set; }

    public double? CallbackRate { get; set; }

    public double? CallbackDuplicateRate { get; set; }

    public double? CallbackRedeliverUntilS { get; set; }

    public bool? SupportsSale { get; set; }

    public bool? ConsistentLookup { get; set; }

    public string? ActionProof { get; set; }

    public double? AuthorizationWindowS { get; set; }

    public int? CallbackDelayMs { get; set; }

    public string? CallbackUrl { get; set; }

    public int? Seed { get; set; }
}

public sealed class MockConfigStore
{
    private readonly object _gate = new();
    private MockConfig _config = new();

    public MockConfig Current
    {
        get
        {
            lock (_gate)
            {
                return _config.Clone();
            }
        }
    }

    public void Update(MockConfigUpdate update)
    {
        lock (_gate)
        {
            _config.Apply(update);
        }
    }
}

public sealed class MockAccountUpdate
{
    public int? RateLimitPerS { get; set; }

    public double? KeyRetentionS { get; set; }

    public bool? Revoked { get; set; }

    public string? CallbackUrl { get; set; }

    public string? SigningSecret { get; set; }
}

public sealed class MockAccountState
{
    private readonly object _gate = new();
    private long _windowStartTicks;
    private int _count;

    public int? RateLimitPerS { get; set; }

    public double? KeyRetentionS { get; set; }

    public bool Revoked { get; set; }

    public string? CallbackUrl { get; set; }

    public string? SigningSecret { get; set; }

    public void Apply(MockAccountUpdate update)
    {
        if (update.RateLimitPerS is { } rateLimitPerS)
        {
            RateLimitPerS = Math.Max(1, rateLimitPerS);
        }

        if (update.KeyRetentionS is { } keyRetentionS)
        {
            KeyRetentionS = Math.Max(0, keyRetentionS);
        }

        if (update.Revoked is { } revoked)
        {
            Revoked = revoked;
        }

        if (update.CallbackUrl is { } callbackUrl)
        {
            CallbackUrl = callbackUrl.Length == 0 ? null : callbackUrl;
        }

        if (update.SigningSecret is { } signingSecret)
        {
            SigningSecret = signingSecret.Length == 0 ? null : signingSecret;
        }
    }

    public bool TryAcquire(int defaultRateLimitPerS, DateTimeOffset now)
    {
        lock (_gate)
        {
            var limit = RateLimitPerS ?? defaultRateLimitPerS;
            if (limit <= 0)
            {
                return false;
            }

            var windowStart = new DateTimeOffset(Interlocked.Read(ref _windowStartTicks), TimeSpan.Zero);
            if (now - windowStart >= TimeSpan.FromSeconds(1))
            {
                Interlocked.Exchange(ref _windowStartTicks, now.UtcTicks);
                _count = 0;
            }

            if (_count >= limit)
            {
                return false;
            }

            _count++;
            return true;
        }
    }
}

public sealed class ActionPayload
{
    public string Type { get; init; } = "three_d_secure";

    public string Url { get; init; } = string.Empty;

    public DateTimeOffset ExpiresAt { get; init; }
}

public sealed record OutcomeDecision(
    string Outcome,
    string? FailureCode,
    ActionPayload? Action,
    bool Hang);

public static class Outcomes
{
    private const string Approved = "approved";
    private const string Declined = "declined";
    private const string ActionRequired = "action_required";
    private const string Error = "error";

    public static OutcomeDecision Decide(string key, MockConfig config)
    {
        var roll = Roll(key, config.Seed, "outcome");
        var threshold = 0.0;

        threshold += config.ErrorRate;
        if (roll < threshold)
        {
            return new OutcomeDecision(Error, "mock_error", null, false);
        }

        threshold += config.DeclineRate;
        if (roll < threshold)
        {
            return new OutcomeDecision(Declined, "insufficient_funds", null, false);
        }

        threshold += config.ActionRate;
        if (roll < threshold)
        {
            var action = new ActionPayload
            {
                Type = "three_d_secure",
                Url = $"https://mock-psp.local/action/{Uri.EscapeDataString(key)}",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            };
            return new OutcomeDecision(ActionRequired, null, action, false);
        }

        var hang = config.ApproveThenHangRate > 0
            && Roll(key, config.Seed, "hang") < config.ApproveThenHangRate;
        return new OutcomeDecision(Approved, null, null, hang);
    }

    public static bool IsApproved(string outcome) =>
        string.Equals(outcome, Approved, StringComparison.Ordinal);

    public static bool Hit(string key, int seed, string tag, double rate) =>
        rate > 0 && Roll(key, seed, tag) < rate;

    public static string Reference(string key, int seed)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"{seed}:reference:{key}")));
        return "psp_" + Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static double Roll(string key, int seed, string tag)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"{seed}:{tag}:{key}")));
        var bucket = BitConverter.ToUInt32(hash, 0);
        return bucket / (double)uint.MaxValue;
    }
}
