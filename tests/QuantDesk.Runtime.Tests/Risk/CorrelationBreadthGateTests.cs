using QuantDesk.Runtime.Risk;

namespace QuantDesk.Runtime.Tests.Risk;

public sealed class CorrelationBreadthGateTests
{
    [Fact]
    public void SevenPositionsMovingTogetherAreNotSevenBets()
    {
        // The measurement that motivated this. On 2026-09-02 the lane held seven crypto symbols at
        // 0.709 mean pairwise correlation -- about 1.33 independent bets carried as if they were
        // seven, with the open-risk limit satisfied at every moment.
        Dictionary<string, IReadOnlyList<decimal>> closes = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < 7; i++) closes[$"S{i}/USD"] = Correlated(seed: i, rho: 0.709);

        CorrelationBreadthDecision decision = CorrelationBreadthGate.Evaluate(
            "S0/USD",
            [.. Enumerable.Range(1, 6).Select(i => $"S{i}/USD")],
            closes,
            positionNotional: 200m,
            maximumCorrelatedExposure: 10_000m);

        Assert.Equal(1_400m, decision.NominalExposure);

        // Seven independent 200-dollar positions would carry 200*sqrt(7) = 529 of exposure. These
        // carry far more, and the effective-bet count collapses toward one.
        Assert.True(decision.CorrelatedExposure > 900m, $"was {decision.CorrelatedExposure}");
        Assert.True(decision.EffectiveBets < 2.5d, $"was {decision.EffectiveBets}");
    }

    [Fact]
    public void IndependentPositionsDiversifyAndAreAllowed()
    {
        Dictionary<string, IReadOnlyList<decimal>> closes = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < 4; i++) closes[$"S{i}/USD"] = Correlated(seed: i, rho: 0.0);

        CorrelationBreadthDecision decision = CorrelationBreadthGate.Evaluate(
            "S0/USD", ["S1/USD", "S2/USD", "S3/USD"], closes, 200m, 10_000m);

        Assert.True(decision.Allowed);

        // Four genuinely independent bets: 200*sqrt(4) = 400, and about four effective bets.
        Assert.True(decision.CorrelatedExposure < 600m, $"was {decision.CorrelatedExposure}");
        Assert.True(decision.EffectiveBets > 2.5d, $"was {decision.EffectiveBets}");
    }

    [Fact]
    public void ACorrelatedBookIsRefusedOnceItPassesTheLimit()
    {
        Dictionary<string, IReadOnlyList<decimal>> closes = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < 5; i++) closes[$"S{i}/USD"] = Correlated(seed: i, rho: 0.95);

        CorrelationBreadthDecision decision = CorrelationBreadthGate.Evaluate(
            "S0/USD", ["S1/USD", "S2/USD", "S3/USD", "S4/USD"], closes, 200m,
            maximumCorrelatedExposure: 600m);

        Assert.False(decision.Allowed);
        Assert.Contains("CorrelatedExposureLimit", decision.Reason);
        Assert.Contains("effective bets", decision.Reason);
    }

    [Fact]
    public void TheFirstPositionIsAlwaysAllowed()
    {
        // A book of one is one bet whatever it is correlated with, and refusing it would stop the
        // lane trading at all rather than stopping it concentrating.
        CorrelationBreadthDecision decision = CorrelationBreadthGate.Evaluate(
            "BTC/USD", [], new Dictionary<string, IReadOnlyList<decimal>>(), 200m, 100m);

        Assert.True(decision.Allowed);
        Assert.Equal(200m, decision.NominalExposure);
    }

    [Fact]
    public void APairWithTooLittleHistoryIsChargedAsIfItWereTheSameBet()
    {
        // Assuming independence on missing data is precisely the error this gate exists to prevent.
        // An unmeasurable pair might be independent or might be the same bet twice, and only one of
        // those two mistakes costs money.
        Dictionary<string, IReadOnlyList<decimal>> closes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["A/USD"] = Correlated(seed: 1, rho: 0.0),
            ["B/USD"] = [100m, 101m, 102m],   // three bars: far too few
        };

        CorrelationBreadthDecision decision =
            CorrelationBreadthGate.Evaluate("A/USD", ["B/USD"], closes, 200m, 10_000m);

        // Two positions charged at correlation 1.0 come to 200*sqrt(4) = 400, not 200*sqrt(2) = 283.
        Assert.Equal(400m, decision.CorrelatedExposure);
        Assert.Equal(1d, decision.EffectiveBets, 3);
    }

    [Fact]
    public void ASymbolWithNoHistoryAtAllIsChargedTheSameWay()
    {
        Dictionary<string, IReadOnlyList<decimal>> closes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["A/USD"] = Correlated(seed: 1, rho: 0.0),
        };

        CorrelationBreadthDecision decision =
            CorrelationBreadthGate.Evaluate("A/USD", ["MISSING/USD"], closes, 200m, 300m);

        Assert.False(decision.Allowed);
    }

    [Fact]
    public void AHedgeThatMovesAgainstTheBookAddsBreadthRatherThanBeingClampedAway()
    {
        // Negative correlations are used as measured. A position that genuinely moves against the
        // book does reduce portfolio exposure, and clamping it to zero would refuse the most useful
        // trade available.
        IReadOnlyList<decimal> series = Correlated(seed: 3, rho: 0.0);
        Dictionary<string, IReadOnlyList<decimal>> closes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["LONG/USD"] = series,
            ["HEDGE/USD"] = Inverted(series),
        };

        CorrelationBreadthDecision decision =
            CorrelationBreadthGate.Evaluate("LONG/USD", ["HEDGE/USD"], closes, 200m, 10_000m);

        // Two perfectly opposed positions of equal size carry almost no net exposure.
        Assert.True(decision.CorrelatedExposure < 210m, $"was {decision.CorrelatedExposure}");
        Assert.True(decision.EffectiveBets > 3d, $"was {decision.EffectiveBets}");
    }

    [Fact]
    public void TheCandidateIsNotDoubleCountedWhenItIsAlreadyHeld()
    {
        Dictionary<string, IReadOnlyList<decimal>> closes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["A/USD"] = Correlated(seed: 1, rho: 0.0),
            ["B/USD"] = Correlated(seed: 2, rho: 0.0),
        };

        CorrelationBreadthDecision decision =
            CorrelationBreadthGate.Evaluate("A/USD", ["A/USD", "B/USD"], closes, 200m, 10_000m);

        Assert.Equal(400m, decision.NominalExposure);
    }

    [Fact]
    public void AFlatSeriesHasNoMeasurableRelationshipAndIsChargedConservatively()
    {
        Dictionary<string, IReadOnlyList<decimal>> closes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["A/USD"] = Correlated(seed: 1, rho: 0.0),
            ["FLAT/USD"] = [.. Enumerable.Repeat(100m, 200)],
        };

        CorrelationBreadthDecision decision =
            CorrelationBreadthGate.Evaluate("A/USD", ["FLAT/USD"], closes, 200m, 10_000m);

        Assert.Equal(400m, decision.CorrelatedExposure);
    }

    /// <summary>
    /// A price series whose returns carry the requested correlation with a shared driver.
    ///
    /// Built from a fixed seed so the test is deterministic: a gate that refuses trades must not
    /// itself be a source of flakiness.
    /// </summary>
    private static IReadOnlyList<decimal> Correlated(int seed, double rho)
    {
        var common = new Random(20260902);
        var own = new Random(seed + 1);
        double weight = Math.Sqrt(Math.Clamp(rho, 0d, 1d));
        double independent = Math.Sqrt(1d - (weight * weight));

        List<decimal> closes = [100m];
        for (int i = 0; i < 200; i++)
        {
            double shared = Gaussian(common);
            double idiosyncratic = Gaussian(own);
            double step = ((weight * shared) + (independent * idiosyncratic)) * 0.002;
            closes.Add(Math.Round(closes[^1] * (decimal)Math.Exp(step), 6));
        }

        return closes;
    }

    private static IReadOnlyList<decimal> Inverted(IReadOnlyList<decimal> closes)
    {
        List<decimal> mirrored = [closes[0]];
        for (int i = 1; i < closes.Count; i++)
        {
            double step = -Math.Log((double)(closes[i] / closes[i - 1]));
            mirrored.Add(Math.Round(mirrored[^1] * (decimal)Math.Exp(step), 6));
        }

        return mirrored;
    }

    private static double Gaussian(Random random)
    {
        double u1 = 1.0 - random.NextDouble();
        double u2 = 1.0 - random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }
}
