using OpenPayments.Checks;
using Xunit;

namespace OpenPayments.Checks.Tests;

public sealed class Ck6TrendTests
{
    [Fact]
    public void Inflight_ramp_to_steady_state_is_not_sustained_growth()
    {
        Assert.False(Ck6Trend.Grew([0, 5, 10, 16, 14, 15, 16], "phase1"));
    }

    [Fact]
    public void Inflight_that_keeps_climbing_is_sustained_growth()
    {
        Assert.True(Ck6Trend.Grew([1, 5, 10, 20, 40, 80], "phase1"));
    }

    [Fact]
    public void Outbox_age_keeps_the_phase3_threshold()
    {
        Assert.True(Ck6Trend.Grew([100, 200, 500, 900], "phase3"));
        Assert.False(Ck6Trend.Grew([100, 200, 250, 260], "phase3"));
    }
}
