using QuantDesk.Domain.Forecasts;

namespace QuantDesk.Domain.Tests.Forecasts;

public sealed class ForecastEdgeTests
{
    [Fact]
    public void ALargeSignalFromAFamilyThatNeverMadeMoneyIsRefused()
    {
        // The conflation this type exists to end. A point forecast of +200 bps was flowing straight
        // into GrossExpectedPnl and being compared against cost, so a big reading from a family with
        // no demonstrated net edge traded. Today's signal cannot supply an edge the family lacks.
        var edge = new ForecastEdge(
            CurrentSignalBps: 200,
            CurrentSignalStandardErrorBps: 10,
            HistoricalNetEdgeBps: -5,
            HistoricalNetEdgeStandardErrorBps: 2,
            HistoricalObservations: 500);

        ForecastEdgeAssessment assessment = ForecastEdgeAssessment.Evaluate(edge, 70);

        Assert.False(assessment.Tradable);
        Assert.Equal("NoDemonstratedHistoricalEdge", assessment.Reason);
    }

    [Fact]
    public void ANoisySignalIsRefusedEvenWhenItsPointEstimateClearsCost()
    {
        // +100 bps looks tradable against a 70 bps cost until the error bar is admitted: at a
        // standard error of 60, the lower bound is roughly +1 bps and the trade is a coin flip.
        var edge = new ForecastEdge(100, 60, 40, 5, 500);

        ForecastEdgeAssessment assessment = ForecastEdgeAssessment.Evaluate(edge, 70);

        Assert.False(assessment.Tradable);
        Assert.Equal("SignalBelowCostAtLowerBound", assessment.Reason);
    }

    [Fact]
    public void APreciseSignalOverADemonstratedEdgeIsTradable()
    {
        var edge = new ForecastEdge(120, 10, 40, 5, 500);

        ForecastEdgeAssessment assessment = ForecastEdgeAssessment.Evaluate(edge, 70);

        Assert.True(assessment.Tradable);
        Assert.Equal(120 - (1.645 * 10), assessment.CurrentSignalLowerBoundBps);
        Assert.Equal(40 - (1.645 * 5), assessment.HistoricalNetEdgeLowerBoundBps);
    }

    [Fact]
    public void AnOmittedStandardErrorIsRefusedRatherThanReadAsCertainty()
    {
        // The failure that made all of this possible. Silence about uncertainty is not a claim that
        // the forecast is exact -- it is the absence of a claim, and treating it as zero error is
        // how a point forecast came to be traded as a fact.
        var edge = new ForecastEdge(120, null, 40, 5, 500);

        ForecastEdgeAssessment assessment = ForecastEdgeAssessment.Evaluate(edge, 70);

        Assert.False(assessment.Tradable);
        Assert.Equal("ForecastUncertaintyNotPublished", assessment.Reason);
        Assert.Null(assessment.CurrentSignalLowerBoundBps);
    }

    [Fact]
    public void AHistoricalEdgeRestingOnTooFewTradesIsRefusedWithThatReason()
    {
        var edge = new ForecastEdge(120, 10, 40, 5, 12);

        Assert.Equal("HistoricalSampleTooSmall", ForecastEdgeAssessment.Evaluate(edge, 70).Reason);
    }

    [Fact]
    public void AnUnpublishedHistoricalEdgeIsRefusedRatherThanAssumedZero()
    {
        var edge = new ForecastEdge(120, 10, null, null, 500);

        Assert.Equal("HistoricalNetEdgeNotPublished", ForecastEdgeAssessment.Evaluate(edge, 70).Reason);
    }

    [Fact]
    public void RaisingTheCostBoundCanTurnATradableEdgeIntoAnUntradableOne()
    {
        // The pairing that makes the arithmetic honest: gross enters at its lower bound and cost at
        // its upper, so both estimates' errors count against trading rather than for it.
        var edge = new ForecastEdge(120, 10, 40, 5, 500);

        Assert.True(ForecastEdgeAssessment.Evaluate(edge, 70).Tradable);
        Assert.False(ForecastEdgeAssessment.Evaluate(edge, 110).Tradable);
    }

    [Fact]
    public void AHistoricalEdgeIndistinguishableFromZeroIsNotAnEdge()
    {
        // Positive point estimate, error bar straddling zero. The family has not demonstrated
        // anything, and the point estimate alone would have said it had.
        var edge = new ForecastEdge(120, 10, 4, 5, 500);

        Assert.Equal("NoDemonstratedHistoricalEdge", ForecastEdgeAssessment.Evaluate(edge, 70).Reason);
    }
}
