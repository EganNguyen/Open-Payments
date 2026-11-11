namespace OpenPayments.Domain;

/// <summary>One settlement file, named by the PSP and belonging to one business date.</summary>
public sealed record SettlementFile(string FileId, DateOnly BusinessDate);

/// <summary>One itemized line from a settlement file (D18).</summary>
public sealed record SettlementLine(
    int LineNo,
    string Kind,
    string? PspReference,
    Money Amount,
    string? Status,
    string? Reference);
