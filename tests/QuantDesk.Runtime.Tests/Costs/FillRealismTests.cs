using QuantDesk.Runtime.Costs;

namespace QuantDesk.Runtime.Tests.Costs;

/// <summary>
/// Pricing one round trip under several fill assumptions, and grading how far the paper result can
/// be trusted.
///
/// Everything this system has reported so far is a single fill assumption with no grade: the
/// broker's paper engine filled, and that fill was taken as the answer. It is one draw from a
/// distribution whose spread is wider than most of the edges being argued about -- the one round
/// trip measured end to end on 2026-09-02 cost 81.2 bps all-in while the price moved 46.6 bps in
/// the rule's favour, and the account still lost.
/// </summary>
public sealed class FillRealismTests
{
    [Fact]
    public void TheAssumptionsAreOrderedFromGenerousToPunishing()
    {
        // If they were not, the choice of assumption would not mean anything.
        IReadOnlyList<FillScenario> scenarios = FillRealism.Scenarios(0.0020d, 200m, 1m);

        Assert.Equal(4, scenarios.Count);
        for (int i = 1; i < scenarios.Count; i++)
            Assert.True(scenarios[i].CostBps >= scenarios[i - 1].CostBps);
    }

    [Fact]
    public void TheMidpointAssumptionPaysNoSpreadAtAll()
    {
        // Which is exactly why it is not a fill. No taker gets the mid on both legs.
        FillScenario midpoint = FillRealism.Scenarios(0.0020d, 200m, 1m)
            .First(s => s.Assumption is FillAssumption.MidpointFavourable);

        Assert.Equal(FillRealism.VenueFeeRoundTripBps, midpoint.CostBps, precision: 9);
    }

    [Fact]
    public void TheAdverseAssumptionPaysTheSpreadOnBothLegs()
    {
        // A marketable order crosses on the way in and on the way out. That is two spreads, not one.
        FillScenario adverse = FillRealism.Scenarios(0.0020d, 200m, 1m)
            .First(s => s.Assumption is FillAssumption.AdverseFullSpread);

        Assert.Equal(FillRealism.VenueFeeRoundTripBps + 40d, adverse.CostBps, precision: 9);
    }

    [Fact]
    public void TheVenueFeeIsChargedUnderEveryAssumption()
    {
        // The fee is not a modelling choice. It is charged in kind on every fill however the fill
        // happened, measured at a median 25.0 bps per entry across 62 round trips.
        Assert.All(
            FillRealism.Scenarios(0d, 200m, 1m),
            scenario => Assert.True(scenario.CostBps >= FillRealism.VenueFeeRoundTripBps));
    }

    [Fact]
    public void TheChoiceOfAssumptionCanDecideTheSignOfTheResult()
    {
        // The reason one number is not enough. On a 200 dollar trip that earned 1.20 frictionless,
        // filling at the mid keeps a profit and filling at the touch does not.
        IReadOnlyList<FillScenario> scenarios = FillRealism.Scenarios(0.0030d, 200m, 1.20m);

        Assert.True(scenarios.First(s => s.Assumption is FillAssumption.MidpointFavourable).AdjustedPnl > 0m);
        Assert.True(scenarios.First(s => s.Assumption is FillAssumption.AdverseFullSpread).AdjustedPnl < 0m);
    }

    [Fact]
    public void TheRealismAdjustmentIsMeasuredAgainstTheTakerCaseNotTheWorstCase()
    {
        // A marketable order really does cross on both legs, so that is the honest baseline for
        // this lane. The maker case describes a strategy the system does not run and would
        // overstate the adjustment.
        decimal adjustment = FillRealism.AdditionalRealismCost(0.0020d, 200m, 1m);

        Assert.Equal(200m * 0.0040m, adjustment, precision: 9);
    }

    [Fact]
    public void ANoSpreadBookNeedsNoRealismAdjustment()
    {
        Assert.Equal(0m, FillRealism.AdditionalRealismCost(0d, 200m, 1m));
    }

    // ------------------------------------------------------------------- grading

    [Fact]
    public void ATightBookOnAHealthyFeedGradesA()
    {
        SimulationGrade grade = FillRealism.Grade(
            relativeSpread: 0.0005d, quoteHealthy: true, volumeCoverage: 1d,
            notional: 200m, restingDepthNotional: 5_000m);

        Assert.Equal(SimulationQuality.A, grade.Grade);
        Assert.Empty(grade.Reasons);
    }

    [Fact]
    public void AnUnhealthyQuoteGradesDWhateverElseIsTrue()
    {
        // A fill reported against an unusable quote did not happen at a price anyone could have
        // traded at, so nothing downstream should read it as an outcome.
        SimulationGrade grade = FillRealism.Grade(
            relativeSpread: 0.0001d, quoteHealthy: false, volumeCoverage: 1d,
            notional: 1m, restingDepthNotional: 1_000_000m);

        Assert.Equal(SimulationQuality.D, grade.Grade);
    }

    [Fact]
    public void AFeedThatBarelyReportsVolumeDegradesTheGrade()
    {
        // 65.6% of crypto bars carry no volume, from 12.6% on BTC to 91.3% on BCH. A result built
        // on a mostly absent series is not a precise result about a quiet market.
        SimulationGrade grade = FillRealism.Grade(
            relativeSpread: 0.0005d, quoteHealthy: true, volumeCoverage: 0.1d,
            notional: 200m, restingDepthNotional: 5_000m);

        Assert.Equal(SimulationQuality.B, grade.Grade);
        Assert.Contains(grade.Reasons, reason => reason.Contains("volume"));
    }

    [Fact]
    public void SizeTheTouchCouldNotAbsorbDegradesTheGrade()
    {
        // It would have walked the book, and a paper engine filling it at the touch is describing
        // a trade that could not have happened as reported.
        SimulationGrade grade = FillRealism.Grade(
            relativeSpread: 0.0005d, quoteHealthy: true, volumeCoverage: 1d,
            notional: 10_000m, restingDepthNotional: 500m);

        Assert.Equal(SimulationQuality.B, grade.Grade);
        Assert.Contains(grade.Reasons, reason => reason.Contains("resting"));
    }

    [Fact]
    public void TwoDegradedTermsGradeC()
    {
        SimulationGrade grade = FillRealism.Grade(
            relativeSpread: 0.0060d, quoteHealthy: true, volumeCoverage: 0.1d,
            notional: 200m, restingDepthNotional: 5_000m);

        Assert.Equal(SimulationQuality.C, grade.Grade);
        Assert.True(grade.Reasons.Count >= 2);
    }

    [Fact]
    public void EveryReasonIsListedSoTheGradeCanBeArguedWith()
    {
        // A grade nobody can audit is an assertion. Each term that degraded it is named.
        SimulationGrade grade = FillRealism.Grade(
            relativeSpread: 0.0060d, quoteHealthy: true, volumeCoverage: 0.1d,
            notional: 10_000m, restingDepthNotional: 500m);

        Assert.Equal(3, grade.Reasons.Count);
    }

    [Fact]
    public void AnUnreadBookDoesNotCountAgainstTheGrade()
    {
        // Not knowing the depth is not evidence that the depth was insufficient.
        SimulationGrade grade = FillRealism.Grade(
            relativeSpread: 0.0005d, quoteHealthy: true, volumeCoverage: 1d,
            notional: 10_000m, restingDepthNotional: null);

        Assert.Equal(SimulationQuality.A, grade.Grade);
    }
}
