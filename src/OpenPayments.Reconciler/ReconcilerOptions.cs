namespace OpenPayments.Reconciler;

/// <summary>The timer values fixed in the Stage 2 design gate.</summary>
public sealed record ReconcilerOptions
{
    public TimeSpan Tick { get; init; } = TimeSpan.FromSeconds(2);

    public int Batch { get; init; } = 100;

    public TimeSpan Lease { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan ParkedLimit { get; init; } = TimeSpan.FromMinutes(15);

    public TimeSpan EscalateAfter { get; init; } = TimeSpan.FromHours(2);

    public TimeSpan KeyRetention { get; init; } = TimeSpan.FromHours(24);

    public TimeSpan EventRetention { get; init; } = TimeSpan.FromDays(7);

    public static ReconcilerOptions FromConfiguration(IConfiguration configuration)
    {
        var options = new ReconcilerOptions
        {
            KeyRetention = TimeSpan.FromHours(
                configuration.GetValue("RECONCILER_KEY_RETENTION_HOURS", 24.0)),
            ParkedLimit = TimeSpan.FromMinutes(
                configuration.GetValue("RECONCILER_PARKED_LIMIT_MINUTES", 15.0)),
        };
        if (options.KeyRetention < TimeSpan.FromHours(24))
        {
            throw new InvalidOperationException(
                "Key retention below 24 hours is refused (D19).");
        }

        return options;
    }
}
