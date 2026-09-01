using QuantDesk.Domain.Forecasts;

namespace QuantDesk.Domain.Tests.Forecasts;

public sealed class ForecastEdgeTests
{
    /// <summary>What the research plane deducted before publishing its point forecast.</summary>
    private const double AssumedCost = 68d;

    [Fact]
    public void ResearchsCostAssumptionIsAddedBackSoItIsNotChargedTwice()
    {
        // The trap that would have silenced the lane. The published point forecast is already net of
        // the cost research assumed, so comparing it directly against a measured cost charges the
        // same cost twice. A signal worth 100 bps gross against a 68 bps cost would be read as 32
        // against 68 and refused, and every trade would be refused the same way -- a gate that looks
        // principled while rejecting everything.
        var edge = Edge(currentSignalNetOfAssumedCost: 32d, standardError: 1d);

        // Gross is 100. Discounted by 1.645 x 1, it clears a 68 bps measured cost with room.
        Assert.Equal(100d - 1.645d, edge.CurrentSignalLowerBoundBps!.Value, precision: 6);
        Assert.True(ForecastEdgeAssessment.Evaluate(edge, AssumedCost).Tradable);
    }

    [Fact]
    public void MeasuredCostReplacesTheAssumptionRatherThanCompoundingWithIt()
    {
        // The reason to add it back rather than leave it embedded: execution is the only side that
        // can measure a round trip, so its figure must be the one that governs. The same forecast is
        // tradable at the assumed cost and refused once the measurement comes in higher.
        var edge = Edge(currentSignalNetOfAssumedCost: 32d, standardError: 1d);

        Assert.True(ForecastEdgeAssessment.Evaluate(edge, 68d).Tradable);
        Assert.False(ForecastEdgeAssessment.Evaluate(edge, 120d).Tradable);
    }

    [Fact]
    public void ALargeSignalFromAFamilyThatNeverMadeMoneyIsRefused()
    {
        // The conflation this type exists to end. A point forecast was flowing straight into
        // GrossExpectedPnl and being compared against cost, so a big reading from a family with no
        // demonstrated net edge traded. Today's signal cannot supply an edge the family lacks.
        var edge = Edge(200d, standardError: 10d, historicalEdge: -5d, historicalError: 2d);

        ForecastEdgeAssessment assessment = ForecastEdgeAssessment.Evaluate(edge, AssumedCost);

        Assert.False(assessment.Tradable);
        Assert.Equal("NoDemonstratedHistoricalEdge", assessment.Reason);
    }

    [Fact]
    public void ANoisySignalIsRefusedEvenWhenItsPointEstimateClearsCost()
    {
        // Gross 100 against a 70 bps cost looks tradable until the error bar is admitted: at a
        // standard error of 60 the lower bound is roughly +1 bps, and the trade is a coin flip.
        var edge = Edge(currentSignalNetOfAssumedCost: 32d, standardError: 60d);

        ForecastEdgeAssessment assessment = ForecastEdgeAssessment.Evaluate(edge, 70d);

        Assert.False(assessment.Tradable);
        Assert.Equal("SignalBelowCostAtLowerBound", assessment.Reason);
    }

    [Fact]
    public void APreciseSignalOverADemonstratedEdgeIsTradable()
    {
        var edge = Edge(currentSignalNetOfAssumedCost: 52d, standardError: 10d);

        ForecastEdgeAssessment assessment = ForecastEdgeAssessment.Evaluate(edge, 70d);

        Assert.True(assessment.Tradable);
        Assert.Equal(52d + AssumedCost - (1.645 * 10), assessment.CurrentSignalLowerBoundBps);
        Assert.Equal(40 - (1.645 * 5), assessment.HistoricalNetEdgeLowerBoundBps);
    }

    [Fact]
    public void AnOmittedStandardErrorIsRefusedRatherThanReadAsCertainty()
    {
        // The failure that made all of this possible. Silence about uncertainty is not a claim that
        // the forecast is exact -- it is the absence of a claim, and treating it as zero error is
        // how a point forecast came to be traded as a fact.
        var edge = Edge(52d, standardError: null);

        ForecastEdgeAssessment assessment = ForecastEdgeAssessment.Evaluate(edge, 70d);

        Assert.False(assessment.Tradable);
        Assert.Equal("ForecastUncertaintyNotPublished", assessment.Reason);
        Assert.Null(assessment.CurrentSignalLowerBoundBps);
    }

    [Fact]
    public void AnOmittedCostAssumptionIsRefusedBecauseTheSignalCannotBeReGrossed()
    {
        // Without knowing what was already deducted there is no way to compare the signal to a
        // measured cost at all. Guessing would reintroduce the double charge silently.
        var edge = Edge(52d, standardError: 10d) with { AssumedRoundTripCostBps = null };

        Assert.Equal("ForecastUncertaintyNotPublished",
            ForecastEdgeAssessment.Evaluate(edge, 70d).Reason);
    }

    [Fact]
    public void AHistoricalEdgeRestingOnTooFewTradesIsRefusedWithThatReason()
    {
        var edge = Edge(52d, standardError: 10d) with { HistoricalObservations = 12 };

        Assert.Equal("HistoricalSampleTooSmall", ForecastEdgeAssessment.Evaluate(edge, 70d).Reason);
    }

    [Fact]
    public void AnUnpublishedHistoricalEdgeIsRefusedRatherThanAssumedZero()
    {
        var edge = Edge(52d, standardError: 10d) with
        {
            HistoricalNetEdgeBps = null,
            HistoricalNetEdgeStandardErrorBps = null,
        };

        Assert.Equal("HistoricalNetEdgeNotPublished", ForecastEdgeAssessment.Evaluate(edge, 70d).Reason);
    }

    [Fact]
    public void RaisingTheCostBoundCanTurnATradableEdgeIntoAnUntradableOne()
    {
        // The pairing that makes the arithmetic honest: gross enters at its lower bound and cost at
        // its upper, so both estimates' errors count against trading rather than for it.
        var edge = Edge(currentSignalNetOfAssumedCost: 52d, standardError: 10d);

        Assert.True(ForecastEdgeAssessment.Evaluate(edge, 70d).Tradable);
        Assert.False(ForecastEdgeAssessment.Evaluate(edge, 180d).Tradable);
    }

    [Fact]
    public void AHistoricalEdgeIndistinguishableFromZeroIsNotAnEdge()
    {
        // Positive point estimate, error bar straddling zero. The family has not demonstrated
        // anything, and the point estimate alone would have said it had.
        var edge = Edge(52d, standardError: 10d, historicalEdge: 4d, historicalError: 5d);

        Assert.Equal("NoDemonstratedHistoricalEdge", ForecastEdgeAssessment.Evaluate(edge, 70d).Reason);
    }

    private static ForecastEdge Edge(
        double currentSignalNetOfAssumedCost,
        double? standardError,
        double? historicalEdge = 40d,
        double? historicalError = 5d) =>
        new(currentSignalNetOfAssumedCost, standardError, historicalEdge, historicalError,
            HistoricalObservations: 500, AssumedRoundTripCostBps: AssumedCost);
}
