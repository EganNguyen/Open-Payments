using System.Globalization;
using System.Text;

namespace OpenPayments.MockPsp;

/// <summary>The mock's itemized settlement file, with the defects C40 names (Task 24).</summary>
public static class MockSettlement
{
    public static string Csv(
        IReadOnlyList<MockAttempt> attempts,
        DateOnly businessDate,
        IReadOnlySet<string> defects)
    {
        var rows = attempts
            .Where(a => DateOnly.FromDateTime(a.CreatedAt.UtcDateTime) == businessDate)
            .OrderBy(a => a.Key, StringComparer.Ordinal)
            .Select(a => new Row(
                a.Op switch { "refund" => "refund", "capture" => "capture", _ => "sale" },
                a.PspReference,
                a.AmountMinor,
                a.Currency,
                a.Outcome,
                a.Key))
            .ToList();

        if (defects.Contains("amount") && rows.Count > 0)
        {
            rows[0] = rows[0] with { AmountMinor = rows[0].AmountMinor + 1 };
        }

        if (defects.Contains("missing") && rows.Count > 0)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        if (defects.Contains("duplicate") && rows.Count > 0)
        {
            rows.Add(rows[0]);
        }

        if (defects.Contains("unmatched"))
        {
            rows.Add(new Row("sale", "psp_unknown_unmatched", 999, "USD", "approved", string.Empty));
        }

        var text = new StringBuilder("line_no,kind,psp_reference,amount_minor,currency,status,reference\n");
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            text.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(row.Kind).Append(',')
                .Append(row.PspReference).Append(',')
                .Append(row.AmountMinor.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(row.Currency).Append(',')
                .Append(row.Status).Append(',')
                .Append(row.Reference).Append('\n');
        }

        return text.ToString();
    }

    private sealed record Row(string Kind, string PspReference, long AmountMinor, string Currency, string Status, string Reference);
}
