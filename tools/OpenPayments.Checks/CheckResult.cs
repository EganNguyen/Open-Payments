namespace OpenPayments.Checks;

public sealed record CheckOptions
{
    public required string Db { get; init; }

    public string? Effects { get; init; }

    public required string Stage { get; init; }

    public string? Prom { get; init; }

    public DateTimeOffset? From { get; init; }

    public DateTimeOffset? To { get; init; }
}

public sealed record CheckResult(
    string Check,
    bool Applicable,
    bool Passed,
    long RowsExamined,
    long Violations,
    IReadOnlyList<string> SampleIds,
    long MoneyImpactMinor,
    string? Reason)
{
    // Wall time spent on this check, so a check that slows as data grows can be found.
    public long DurationMs { get; init; }
}
