using System.Globalization;
using System.Text.Json;

namespace OpenPayments.Domain;

/// <summary>
/// An ISO 4217 currency code with the minor-unit exponent the database stores.
/// Only the currencies in the <c>currencies</c> table are accepted.
/// </summary>
public readonly record struct CurrencyCode
{
    private CurrencyCode(string value, short exponent)
    {
        Value = value;
        Exponent = exponent;
    }

    public string Value { get; }

    public short Exponent { get; }

    public static CurrencyCode Usd { get; } = new("USD", 2);

    public static CurrencyCode Jpy { get; } = new("JPY", 0);

    public static CurrencyCode Kwd { get; } = new("KWD", 3);

    public static bool TryParse(string? value, out CurrencyCode currency)
    {
        if (TryGetExponent(value, out var exponent))
        {
            currency = new CurrencyCode(value!, exponent);
            return true;
        }

        currency = default;
        return false;
    }

    public static CurrencyCode Parse(string value) =>
        TryParse(value, out var currency)
            ? currency
            : throw new ArgumentException($"Unknown currency code '{value}'.", nameof(value));

    public override string ToString() => Value ?? string.Empty;

    private static bool TryGetExponent(string? value, out short exponent)
    {
        switch (value)
        {
            case "USD":
                exponent = 2;
                return true;
            case "JPY":
                exponent = 0;
                return true;
            case "KWD":
                exponent = 3;
                return true;
            default:
                exponent = 0;
                return false;
        }
    }
}

/// <summary>
/// An amount in minor units with its currency. Amounts are never floating point.
/// </summary>
public readonly record struct Money(long Minor, CurrencyCode Currency)
{
    /// <summary>
    /// The starting maximum from <c>api/openapi.yaml</c>, applied after an exact parse.
    /// </summary>
    public const long MaximumMinor = 99_999_999_999;

    public Money Add(Money other)
    {
        if (Currency != other.Currency)
        {
            throw new InvalidOperationException(
                $"Cannot add {other.Currency} to {Currency}.");
        }

        return new Money(checked(Minor + other.Minor), Currency);
    }

    /// <summary>
    /// Parses a JSON number exactly. It never passes through <see cref="double"/> or
    /// <see cref="float"/>, so integers above 2^53 keep every digit until the range check.
    /// </summary>
    public static Result<Money> ParseMinor(JsonElement element, CurrencyCode currency)
    {
        if (element.ValueKind != JsonValueKind.Number)
        {
            return Result<Money>.Fail("invalid_amount");
        }

        if (!decimal.TryParse(
                element.GetRawText(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var amount))
        {
            return Result<Money>.Fail("invalid_amount");
        }

        if (amount <= 0 || amount != decimal.Truncate(amount))
        {
            return Result<Money>.Fail("invalid_amount");
        }

        if (amount > MaximumMinor)
        {
            return Result<Money>.Fail("amount_out_of_range");
        }

        return Result<Money>.Ok(new Money((long)amount, currency));
    }
}
