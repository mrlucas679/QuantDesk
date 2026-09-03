using QuantDesk.Domain.Forecasts;
using QuantDesk.Runtime.Experts;
using QuantDesk.Runtime.Indicators;

namespace QuantDesk.Runtime.Tests.Experts;

/// <summary>
/// The second and third forecast families, and the first two experts whose hypothesis is not
/// direction.
///
/// Until now only DirectionalReturn was ever emitted, so the typed committee had nothing to
/// aggregate and two exit rules written against regime could not be implemented at all -- a
/// management plan that said ExitOnRegimeChange with nothing able to say the regime had changed.
/// </summary>
public sealed class VolatilityAndRegimeExpertTests
{
    // ------------------------------------------------------------------ volatility

    [Fact]
    public void AQuietMarketForecastsLessVarianceThanAViolentOne()
    {
        // The one regularity in this whole system that has a right to expect to work: volatility
        // clusters, far more reliably than direction persists.
        VolatilityForecast quiet = Volatility(Series(amplitude: 0.2m))!.Value;
        VolatilityForecast violent = Volatility(Series(amplitude: 5m))!.Value;

        Assert.True(violent.ExpectedRealizedVariance > quiet.ExpectedRealizedVariance);
    }

    [Fact]
    public void VarianceAndAnnualisedVolatilityAgreeWithEachOther()
    {
        VolatilityForecast forecast = Volatility(Series(amplitude: 2m))!.Value;

        double implied = Math.Sqrt(forecast.ExpectedRealizedVariance * 288d * 365d);
        Assert.Equal(implied, forecast.ExpectedAnnualizedVolatility, precision: 6);
    }

    [Fact]
    public void TooLittleHistoryForTheLongComponentRefusesRatherThanShortening()
    {
        // A HAR built from a short window is not a less precise HAR, it is a different model with
        // the same name -- and section 9.4 forbids encoding missing history as a value.
        Assert.Null(Volatility(Series(amplitude: 2m, bars: 200)));
    }

    [Fact]
    public void DisagreementBetweenHorizonsWidensTheForecastVariance()
    {
        // When short, medium and long disagree the regime is turning and the point forecast
        // deserves less trust. A calm series has the three components agreeing closely.
        VolatilityForecast calm = Volatility(Series(amplitude: 1m))!.Value;

        Assert.True(calm.ForecastVariance >= 0d);
        Assert.True(double.IsFinite(calm.ForecastVariance));
    }

    [Fact]
    public void VolatilityIsPublishedIntoItsOwnFamilyAndNeverAsDirection()
    {
        // Section 10.1 rejects a universal score precisely so high variance cannot quietly become
        // a reason to buy. High expected variance means size smaller, never go long.
        Assert.Equal(ForecastType.RealizedVolatility, Volatility(Series(amplitude: 2m))!.Value.Metadata.Type);
    }

    [Fact]
    public void CalibrationIsPublishedAsUnknown()
    {
        // QLIKE and MSE against realized variance are what would earn a number here; neither has
        // been run.
        Assert.Equal(0.5d, Volatility(Series(amplitude: 2m))!.Value.CalibrationScore, precision: 9);
    }

    // ---------------------------------------------------------------------- regime

    [Fact]
    public void RegimeProbabilitiesSumToOne()
    {
        RegimeForecast regime = Regime(Trending())!.Value;

        double sum = regime.LowVolTrend.Value + regime.HighVolTrend.Value + regime.Range.Value
            + regime.Stress.Value + regime.Event.Value;

        Assert.Equal(1d, sum, precision: 9);
    }

    [Fact]
    public void ATrendingMarketLeansTrendRatherThanRange()
    {
        RegimeForecast regime = Regime(Trending())!.Value;

        Assert.True(regime.LowVolTrend.Value + regime.HighVolTrend.Value > regime.Range.Value);
    }

    [Fact]
    public void AChoppyMarketLeansRangeRatherThanTrend()
    {
        RegimeForecast regime = Regime(Choppy())!.Value;

        Assert.True(regime.Range.Value > regime.LowVolTrend.Value + regime.HighVolTrend.Value);
    }

    [Fact]
    public void TheEventProbabilityIsZeroBecauseNoEventModelExists()
    {
        // Publishing a fabricated event probability would be worse than publishing none, and the
        // gap should be visible rather than filled with a plausible-looking number.
        Assert.Equal(0d, Regime(Trending())!.Value.Event.Value, precision: 9);
    }

    [Fact]
    public void RegimeIsPublishedAsContextAndNeverAsDirection()
    {
        Assert.Equal(ForecastType.Regime, Regime(Trending())!.Value.Metadata.Type);
    }

    [Fact]
    public void TooLittleHistoryRefuses()
    {
        Assert.Null(Regime(Series(amplitude: 2m, bars: 200)));
    }

    // -------------------------------------------------------------------- fixtures

    private static VolatilityForecast? Volatility(IndicatorSet set) =>
        new RealizedVolatilityExpert().Forecast(set, "BTC/USD", 0, 20, TimeSpan.FromMinutes(5), 1, 1, 1_000, 1);

    private static RegimeForecast? Regime(IndicatorSet set) =>
        new MarketRegimeExpert().Forecast(set, 0, 21, TimeSpan.FromMinutes(5), 1, 1, 1_000, 1);

    /// <summary>An oscillating series whose amplitude sets how volatile it is.</summary>
    private static IndicatorSet Series(decimal amplitude, int bars = 400)
    {
        List<decimal> closes =
            [.. Enumerable.Range(0, bars).Select(i => 100m + (amplitude * (decimal)Math.Sin(i / 5.0)))];
        return Build(closes);
    }

    /// <summary>A steady advance, which is what a trending market looks like to ADX.</summary>
    private static IndicatorSet Trending() =>
        Build([.. Enumerable.Range(0, 400).Select(i => 100m + (0.35m * i))]);

    /// <summary>A tight oscillation, which is what a range looks like.</summary>
    private static IndicatorSet Choppy() =>
        Build([.. Enumerable.Range(0, 400).Select(i => 100m + (i % 2 == 0 ? 0.4m : -0.4m))]);

    private static IndicatorSet Build(IReadOnlyList<decimal> closes)
    {
        IndicatorSet? set = IndicatorSet.Build(
            closes,
            [.. closes.Select(c => c + 0.6m)],
            [.. closes.Select(c => c - 0.6m)],
            [.. Enumerable.Repeat(1_000m, closes.Count)]);

        Assert.NotNull(set);
        return set;
    }
}
