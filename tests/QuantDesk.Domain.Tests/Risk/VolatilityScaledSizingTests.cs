using QuantDesk.Domain.Risk;

namespace QuantDesk.Domain.Tests.Risk;

/// <summary>
/// Position size as a function of forecast volatility, which is w = (sigma_target / sigma_hat) * s.
///
/// Every order was a flat notional -- the same $200 whether the instrument was BTC or DIA -- while
/// HAR and GARCH variance models sat fitted per instrument, parity-checked, adopted on every cycle,
/// and read by nothing that sized anything. On 2026-09-03 that meant $200 of AAVE and $200 of ETH
/// against an identical $10 stop, with AAVE moving roughly twice as far per bar: the same stop, at
/// two different distances measured in the instruments' own noise.
/// </summary>
public sealed class VolatilityScaledSizingTests
{
    private const decimal Base = 200m;
    private const decimal Cap = 400m;

    [Fact]
    public void AQuieterInstrumentGetsMoreNotionalThanANoisierOne()
    {
        // The whole point, in one assertion.
        decimal quiet = VolatilityScaledSizing.NotionalFor(Base, 0.01d, 0.008d, Cap);
        decimal noisy = VolatilityScaledSizing.NotionalFor(Base, 0.01d, 0.02d, Cap);

        Assert.True(quiet > noisy);
    }

    [Fact]
    public void AnInstrumentExactlyAtTargetIsTradedAtTheBaseNotional()
    {
        Assert.Equal(Base, VolatilityScaledSizing.NotionalFor(Base, 0.01d, 0.01d, Cap));
    }

    [Fact]
    public void TwoInstrumentsCarryTheSameRiskAfterScaling()
    {
        // What "risk parity" means concretely: notional times its own volatility is equal, even
        // though the notionals are not. A flat notional equalises the wrong quantity.
        const double calm = 0.005d;
        const double wild = 0.01d;

        decimal a = VolatilityScaledSizing.NotionalFor(Base, 0.01d, calm, Cap);
        decimal b = VolatilityScaledSizing.NotionalFor(Base, 0.01d, wild, Cap);

        Assert.Equal((double)a * calm, (double)b * wild, precision: 6);
    }

    [Fact]
    public void ACollapsingVarianceForecastCannotAskForAnUnboundedPosition()
    {
        // A quiet window, a warm-up artefact, or a model fitted on another regime can drive the
        // forecast toward zero, and the ratio toward infinity. Section 20.3 treats a model's output
        // as an input to a bounded decision, never as an instruction.
        decimal sized = VolatilityScaledSizing.NotionalFor(Base, 0.01d, 1e-12d, Cap);

        Assert.Equal(Base * (decimal)VolatilityScaledSizing.MaximumScale, sized);
    }

    [Fact]
    public void AnExtremeVolatilityForecastStillLeavesATradablePosition()
    {
        decimal sized = VolatilityScaledSizing.NotionalFor(Base, 0.01d, 10d, Cap);

        Assert.Equal(Base * (decimal)VolatilityScaledSizing.MinimumScale, sized);
    }

    [Fact]
    public void TheRiskCapIsNeverExceededHoweverQuietTheInstrumentLooks()
    {
        Assert.Equal(250m, VolatilityScaledSizing.NotionalFor(Base, 0.01d, 1e-12d, 250m));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0d)]
    [InlineData(-1d)]
    public void AnUnusableForecastFallsBackToTheSizeThatWouldHaveBeenUsedAnyway(double forecast)
    {
        // Refusing to size would stop the lane during a warm-up or a feed gap, and inventing a
        // scale from a non-finite estimate is the silent-confidence failure the model bridge exists
        // to prevent. The honest fallback is the unscaled size.
        Assert.Equal(Base, VolatilityScaledSizing.NotionalFor(Base, 0.01d, forecast, Cap));
    }

    [Fact]
    public void VarianceBecomesVolatilityOverTheHoldingPeriod()
    {
        // Mean squared log return per bar, over 48 five-minute bars: sqrt(v * n).
        double perBar = 4e-6d;

        Assert.Equal(
            Math.Sqrt(perBar * 48), VolatilityScaledSizing.VolatilityOver(perBar, 48), precision: 12);
    }

    [Theory]
    [InlineData(0d, 48)]
    [InlineData(-1d, 48)]
    [InlineData(double.NaN, 48)]
    [InlineData(4e-6d, 0)]
    public void AnImpossibleVarianceIsNotConvertedIntoAConfidentVolatility(double variance, int bars)
    {
        Assert.True(double.IsNaN(VolatilityScaledSizing.VolatilityOver(variance, bars)));
    }

    [Fact]
    public void ScalingChangesTheSpreadOfOutcomesAndNotTheirMean()
    {
        // Stated so nobody reads this as alpha. A strategy with no edge, sized well, still has no
        // edge: the expected return of a position is proportional to its notional, so scaling both
        // sides of a coin flip leaves a coin flip.
        const double edgeBps = 0.5d;
        decimal sized = VolatilityScaledSizing.NotionalFor(Base, 0.01d, 0.02d, Cap);

        double expectedBefore = (double)Base * edgeBps / 10_000d;
        double expectedAfter = (double)sized * edgeBps / 10_000d;

        Assert.True(expectedAfter < expectedBefore);
        Assert.True(expectedAfter > 0d == expectedBefore > 0d);
    }
}
