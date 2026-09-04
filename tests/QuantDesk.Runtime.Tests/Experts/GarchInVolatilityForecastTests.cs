using QuantDesk.Domain.Contracts;
using QuantDesk.Domain.Forecasts;
using QuantDesk.Runtime.Experts;
using QuantDesk.Runtime.Indicators;
using QuantDesk.Runtime.Research;

namespace QuantDesk.Runtime.Tests.Experts;

/// <summary>
/// The second variance model, and the units gap between it and the first.
///
/// GARCH was fitted, parity-checked against <c>arch</c>, adopted on every cycle and read by nothing
/// -- an artifact with a passing test beside it rather than a safeguard. It now contributes to the
/// volatility forecast's uncertainty, which is the honest use for a second independent estimate of
/// the same quantity: when two models that fail differently agree, the number is worth more.
///
/// The trap these are mostly about is the scale. GARCH is fitted on percent returns; this expert
/// works in mean squared log return. That is a factor of ten thousand, and unconverted it would not
/// look like a units bug -- it would look like two models permanently disagreeing, reported as
/// maximum uncertainty forever by code that appeared to be measuring something.
/// </summary>
public sealed class GarchInVolatilityForecastTests
{
    [Fact]
    public void TheFittedGarchArtifactIsInPercentAndSaysSo()
    {
        // The premise of the conversion. If a refit ever changes the scale, this fails here rather
        // than silently rescaling every forecast's uncertainty by ten thousand.
        GarchVarianceModel garch = LoadGarch();

        Assert.True(garch.IsFitted);
        Assert.Equal("percent", garch.ReturnUnits);
    }

    [Fact]
    public void AgreementBetweenTheTwoModelsLeavesTheForecastTighterThanDisagreement()
    {
        // The property being bought. Identical inputs, one with the second model and one without:
        // the disagreement term can only widen the interval, never narrow it, so a second opinion
        // can never make a forecast look more certain than its own components support.
        decimal[] closes = SteadyCloses(400);

        VolatilityForecast withGarch = Forecast(closes, LoadGarch());
        VolatilityForecast withoutGarch = Forecast(closes, GarchVarianceModel.Unfitted());

        Assert.True(withGarch.ForecastVariance >= withoutGarch.ForecastVariance);

        // And the point forecast is untouched: the second model informs the uncertainty and is
        // never blended into the estimate, which would make a third model nobody fitted.
        Assert.Equal(withoutGarch.ExpectedRealizedVariance, withGarch.ExpectedRealizedVariance, precision: 12);
    }

    [Fact]
    public void TheSecondModelActuallyContributesRatherThanSilentlyDeclining()
    {
        // The anti-vacuity check. Every other assertion here is satisfied by a GARCH that returns
        // null on every bar -- the forecast would simply be the one it always was, the comparisons
        // would hold, and the wiring would be decorative. This is the same failure the fault
        // campaign had for its entire existence, so it is worth one test to rule out.
        decimal[] closes = SteadyCloses(400);

        VolatilityForecast withGarch = Forecast(closes, LoadGarch());
        VolatilityForecast withoutGarch = Forecast(closes, GarchVarianceModel.Unfitted());

        Assert.True(
            withGarch.ForecastVariance > withoutGarch.ForecastVariance,
            "GARCH contributed nothing, so the second model is wired but never consulted.");
    }

    [Fact]
    public void TheDisagreementIsOnTheSameScaleAsTheForecastItself()
    {
        // The units check with teeth. On a quiet series both models describe a small variance, so
        // their disagreement must be small too. Unconverted, GARCH would be ten thousand times
        // larger and this would exceed the forecast by orders of magnitude.
        decimal[] closes = SteadyCloses(400);

        VolatilityForecast forecast = Forecast(closes, LoadGarch());

        Assert.True(forecast.ExpectedRealizedVariance > 0d);
        Assert.True(
            forecast.ForecastVariance < 1d,
            $"Forecast variance {forecast.ForecastVariance} is not on a squared-log-return scale.");
    }

    [Fact]
    public void AModelFittedOnAScaleNothingConvertsIsRefusedRatherThanAssumedToMatch()
    {
        // Silence, not a guess. A model whose returns are on an unknown scale is not evidence about
        // this instrument, and treating its number as comparable is the whole failure mode.
        FittedModelContract artifact = GarchArtifact();
        FittedModelContract relabelled = artifact with
        {
            Variant = artifact.Variant.ToDictionary(
                pair => pair.Key,
                pair => pair.Key == "return_units" ? "basis_points" : pair.Value,
                StringComparer.Ordinal),
        };

        GarchVarianceModel.TryLoad(relabelled, Runtime(relabelled), out GarchVarianceModel mislabelled, out _);

        decimal[] closes = SteadyCloses(400);
        VolatilityForecast withMislabelled = Forecast(closes, mislabelled);
        VolatilityForecast withoutGarch = Forecast(closes, GarchVarianceModel.Unfitted());

        Assert.Equal(withoutGarch.ForecastVariance, withMislabelled.ForecastVariance, precision: 12);
    }

    [Fact]
    public void TooLittleHistoryForTheWarmUpLeavesTheForecastAsItWas()
    {
        // GARCH warms over 289 bars against HAR's 288, so there is a window where one model answers
        // and the other cannot. That is not a failure and must not suppress the forecast.
        GarchVarianceModel garch = LoadGarch();
        decimal[] closes = SteadyCloses(garch.WarmupBars);

        VolatilityForecast withGarch = Forecast(closes, garch);
        VolatilityForecast withoutGarch = Forecast(closes, GarchVarianceModel.Unfitted());

        Assert.Equal(withoutGarch.ForecastVariance, withGarch.ForecastVariance, precision: 12);
    }

    // ------------------------------------------------------------------------------- fixtures

    private static VolatilityForecast Forecast(IReadOnlyList<decimal> closes, GarchVarianceModel garch)
    {
        var expert = new RealizedVolatilityExpert(new StubModels(garch));
        VolatilityForecast? forecast = expert.Forecast(
            Build(closes),
            symbol: "BTC/USD",
            instrumentSlot: 0,
            expertId: 7,
            horizon: TimeSpan.FromMinutes(5),
            eventNs: 1_000L,
            nowMonotonicTicks: 0L,
            validUntilMonotonicTicks: long.MaxValue,
            sourceStateVersion: 1);

        return forecast ?? throw new InvalidOperationException("The expert declined to forecast.");
    }

    /// <summary>A gently drifting series, so realised variance is small but not zero.</summary>
    private static decimal[] SteadyCloses(int count)
    {
        var closes = new decimal[count];
        decimal price = 30_000m;
        for (int index = 0; index < count; index++)
        {
            // Deterministic, alternating, and tiny: a few basis points a bar.
            price *= index % 2 == 0 ? 1.0003m : 0.9997m;
            closes[index] = price;
        }

        return closes;
    }

    private static IndicatorSet Build(IReadOnlyList<decimal> closes)
    {
        IndicatorSet? set = IndicatorSet.Build(
            closes,
            [.. closes.Select(close => close * 1.0002m)],
            [.. closes.Select(close => close * 0.9998m)],
            [.. Enumerable.Repeat(1_000m, closes.Count)]);

        Assert.NotNull(set);
        return set;
    }

    private static GarchVarianceModel LoadGarch()
    {
        FittedModelContract artifact = GarchArtifact();
        GarchVarianceModel.TryLoad(artifact, Runtime(artifact), out GarchVarianceModel model, out _);
        return model;
    }

    private static FittedModelContract GarchArtifact() =>
        FittedModelArtifactReader.ReadFile(
            Path.Combine(FixtureRoot, "garch-conditional-variance.json"));

    private static RuntimeFeatureContract Runtime(FittedModelContract artifact) => new(
        artifact.FeatureSchemaHash,
        artifact.FeatureSemantics!.Units,
        artifact.FeatureSemantics.MissingPolicy,
        artifact.FeatureSemantics.BarDurationMinutes);

    private static readonly string FixtureRoot = LocateFixtures();

    private static string LocateFixtures()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, ".git")))
            directory = directory.Parent;
        return Path.Combine(
            directory?.FullName ?? AppContext.BaseDirectory,
            "tests", "fixtures", "model-artifacts");
    }

    /// <summary>A model source carrying a GARCH artifact and no HAR, so the fallback weights apply.</summary>
    private sealed class StubModels(GarchVarianceModel garch) : IFittedModelSource
    {
        public HarVarianceModel Har(string symbol, int barDurationMinutes) =>
            HarVarianceModel.Unfitted();

        public GarchVarianceModel Garch(string symbol, int barDurationMinutes) => garch;
    }
}
