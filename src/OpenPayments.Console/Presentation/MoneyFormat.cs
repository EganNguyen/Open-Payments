using System.Collections.Frozen;
using System.Globalization;
using System.Text;

namespace OpenPayments.Console.Presentation;

/// <summary>
/// Minor units to text and back, with integer arithmetic only. Decimal places come from the
/// currencies table; the defaults match 0001_initial.sql until the service loads the table.
/// </summary>
public static class MoneyFormat
{
    private static FrozenDictionary<string, int> s_exponents = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["USD"] = 2,
        ["JPY"] = 0,
        ["KWD"] = 3,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    public static IReadOnlyDictionary<string, int> Exponents => s_exponents;

    public static void Load(IReadOnlyDictionary<string, int> exponents) =>
        s_exponents = exponents.ToFrozenDictionary(StringComparer.Ordinal);

    public static int Exponent(string currency) =>
        s_exponents.TryGetValue(currency, out var exponent)
            ? exponent
            : throw new ArgumentException($"Unknown currency '{currency}'.", nameof(currency));

    /// <summary>USD 1050 is "10.50", JPY 1050 is "1,050", KWD 1050 is "1.050".</summary>
    public static string Display(long minor, string currency)
    {
        var exponent = Exponent(currency);
        var negative = minor < 0;
        // Work on the magnitude as an unsigned value, so long.MinValue is safe.
        var magnitude = negative ? (ulong)(-(minor + 1)) + 1 : (ulong)minor;
        var digits = magnitude.ToString(CultureInfo.InvariantCulture).PadLeft(exponent + 1, '0');
        var whole = digits[..^exponent];
        var fraction = exponent == 0 ? string.Empty : digits[^exponent..];

        var text = new StringBuilder();
        if (negative)
        {
            text.Append('-');
        }

        for (var i = 0; i < whole.Length; i++)
        {
            if (i > 0 && (whole.Length - i) % 3 == 0)
            {
                text.Append(',');
            }

            text.Append(whole[i]);
        }

        if (exponent > 0)
        {
            text.Append('.').Append(fraction);
        }

        return text.ToString();
    }

    public static string DisplayWithCode(long minor, string currency) => Display(minor, currency) + " " + currency;

    /// <summary>
    /// Parses a positive amount such as "1,000.50". Refuses more decimals than the currency has,
    /// misplaced group separators, signs, exponents and overflow, so "10.50" can't become the wrong
    /// minor units for JPY.
    /// </summary>
    public static bool TryParse(string? input, string currency, out long minor)
    {
        minor = 0;
        if (string.IsNullOrWhiteSpace(input) || !s_exponents.TryGetValue(currency, out var exponent))
        {
            return false;
        }

        var text = input.Trim();
        var point = text.IndexOf('.', StringComparison.Ordinal);
        var whole = point < 0 ? text : text[..point];
        var fraction = point < 0 ? string.Empty : text[(point + 1)..];
        if (point >= 0 && (fraction.Length == 0 || exponent == 0))
        {
            return false;
        }

        if (fraction.Length > exponent || !fraction.All(char.IsAsciiDigit))
        {
            return false;
        }

        if (whole.Contains(',', StringComparison.Ordinal))
        {
            var groups = whole.Split(',');
            if (groups[0].Length is < 1 or > 3 || groups.Skip(1).Any(group => group.Length != 3))
            {
                return false;
            }

            whole = string.Concat(groups);
        }

        if (whole.Length == 0 || !whole.All(char.IsAsciiDigit))
        {
            return false;
        }

        var digits = whole + fraction.PadRight(exponent, '0');
        if (!long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out minor))
        {
            minor = 0;
            return false;
        }

        return true;
    }
}
