using System.Numerics;
using System.Text.Json;
using FsCheck.Xunit;
using OpenPayments.Domain;
using Xunit;

namespace OpenPayments.Domain.Tests;

public sealed class MoneyTests
{
    [Property]
    public bool U1_add_is_commutative_same_currency(long a, long b)
    {
        var sum = (BigInteger)a + b;
        if (sum > long.MaxValue || sum < long.MinValue)
        {
            return true;
        }

        var left = new Money(a, CurrencyCode.Usd).Add(new Money(b, CurrencyCode.Usd));
        var right = new Money(b, CurrencyCode.Usd).Add(new Money(a, CurrencyCode.Usd));
        return left == right;
    }

    [Fact]
    public void U1_add_mixed_currency_throws()
    {
        var usd = new Money(100, CurrencyCode.Usd);
        var jpy = new Money(100, CurrencyCode.Jpy);

        Assert.Throws<InvalidOperationException>(() => usd.Add(jpy));
    }

    [Fact]
    public void U1_add_overflow_throws_never_wraps()
    {
        var maximum = new Money(long.MaxValue, CurrencyCode.Usd);
        var one = new Money(1, CurrencyCode.Usd);

        Assert.Throws<OverflowException>(() => maximum.Add(one));
    }

    [Theory]
    [InlineData("JPY", 0)]
    [InlineData("USD", 2)]
    [InlineData("KWD", 3)]
    public void U1_exponent_per_currency(string code, int exponent)
    {
        Assert.True(CurrencyCode.TryParse(code, out var currency));
        Assert.Equal(exponent, currency.Exponent);
    }

    [Theory]
    [InlineData("0", null)]
    [InlineData("-1", null)]
    [InlineData("1.5", null)]
    [InlineData("9007199254740993", null)]
    [InlineData("1e3", 1000L)]
    public void E11_rejected_or_exact(string json, long? expectedMinor)
    {
        using var document = JsonDocument.Parse(json);

        var result = Money.ParseMinor(document.RootElement, CurrencyCode.Usd);

        if (expectedMinor is null)
        {
            Assert.False(result.IsSuccess);
            Assert.NotNull(result.ErrorCode);
        }
        else
        {
            Assert.True(result.IsSuccess);
            Assert.Equal(expectedMinor.Value, result.Value.Minor);
        }
    }
}
