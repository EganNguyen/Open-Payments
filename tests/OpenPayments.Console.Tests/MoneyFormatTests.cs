using OpenPayments.Console.Presentation;
using Xunit;

namespace OpenPayments.Console.Tests;

public sealed class MoneyFormatTests
{
    [Theory]
    [InlineData(1050L, "USD", "10.50")]
    [InlineData(1050L, "JPY", "1,050")]
    [InlineData(1050L, "KWD", "1.050")]
    [InlineData(5L, "USD", "0.05")]
    [InlineData(0L, "KWD", "0.000")]
    [InlineData(100050L, "USD", "1,000.50")]
    [InlineData(-1050L, "USD", "-10.50")]
    public void Display_respects_currency_decimals(long minor, string currency, string expected) =>
        Assert.Equal(expected, MoneyFormat.Display(minor, currency));

    [Theory]
    [InlineData("10.50", "JPY", false, 0L)]
    [InlineData("10.505", "USD", false, 0L)]
    [InlineData("1,000.50", "USD", true, 100050L)]
    [InlineData("10.5", "USD", true, 1050L)]
    [InlineData("10", "USD", true, 1000L)]
    [InlineData("1,050", "JPY", true, 1050L)]
    [InlineData("1.050", "KWD", true, 1050L)]
    [InlineData("1,00.50", "USD", false, 0L)]
    [InlineData("-5", "USD", false, 0L)]
    [InlineData("1e3", "USD", false, 0L)]
    [InlineData("10.", "USD", false, 0L)]
    [InlineData("", "USD", false, 0L)]
    [InlineData("10", "XXX", false, 0L)]
    public void TryParse_refuses_too_many_decimals(string input, string currency, bool ok, long minor)
    {
        Assert.Equal(ok, MoneyFormat.TryParse(input, currency, out var parsed));
        Assert.Equal(minor, parsed);
    }

    [Fact]
    public void Display_and_parse_handle_values_above_two_to_the_53()
    {
        const long big = 9_007_199_254_740_993; // 2^53 + 1, not exact as a double
        var text = MoneyFormat.Display(big, "USD");
        Assert.Equal("90,071,992,547,409.93", text);
        Assert.True(MoneyFormat.TryParse(text, "USD", out var back));
        Assert.Equal(big, back);
        Assert.Equal("-92,233,720,368,547,758.08", MoneyFormat.Display(long.MinValue, "USD"));
        Assert.False(MoneyFormat.TryParse("92,233,720,368,547,758.08", "USD", out _));
    }
}
