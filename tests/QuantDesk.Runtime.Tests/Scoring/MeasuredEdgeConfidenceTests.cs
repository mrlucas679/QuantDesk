using QuantDesk.Runtime.Scoring;

namespace QuantDesk.Runtime.Tests.Scoring;

/// <summary>
/// What a vote is worth, from the record rather than from a literal.
///
/// The committee weights every vote and averages those weights into an agreement score it then
/// compares against a floor. Both numbers arrived as constants -- a calibration of 0.75 and a weight
/// of 0.5, on every vote, by every expert, for every instrument -- so the floor was being tested
/// against a literal and the scorer's measured output reached no decision at all.
/// </summary>
public sealed class MeasuredEdgeConfidenceTests
{
    [Fact]
    public void AZeroEdgeIsWorthExactlyTheUnmeasuredDefault()
    {
        // The property that makes this the right scalar rather than one more dial. A rule measured
        // at no edge and a rule with no record are equally uninformative, and it is right that they
        // weigh the same -- so the unmeasured default is derived here rather than asserted.
        Assert.Equal(
            ForecastCalibration.Unmeasured,
            MeasuredEdgeConfidence.From(meanNetBps: 0d, lowerBoundBps: -20d),
            precision: 6);
    }

    [Fact]
    public void ARuleMeasuredBelowItsVenueCostIsTrustedLessThanOneWithNoRecord()
    {
        // The case that was live. Every rule in both books measures negative against the venue's
        // real round trip, and each was voting at a hardcoded 0.75 -- more confidence than an
        // unmeasured rule got, on evidence that said it loses money.
        double measured = MeasuredEdgeConfidence.From(meanNetBps: -25d, lowerBoundBps: -45d);

        Assert.True(measured < ForecastCalibration.Unmeasured);
        Assert.True(measured < 0.75d);
    }

    [Fact]
    public void ADemonstratedEdgeClearsTheCommitteeFloorAndAMarginalOneDoesNot()
    {
        // The floor reads as a quantity rather than a fraction: 0.60 admits a rule whose net edge
        // is about a quarter of a standard error above zero.
        const double committeeFloor = 0.60d;

        // +40 bps with a 10 bps standard error: four sigma of demonstrated edge.
        Assert.True(
            MeasuredEdgeConfidence.From(meanNetBps: 40d, lowerBoundBps: 40d - (1.96d * 10d))
                > committeeFloor);

        // +1 bps with a 20 bps standard error: an edge indistinguishable from none.
        Assert.False(
            MeasuredEdgeConfidence.From(meanNetBps: 1d, lowerBoundBps: 1d - (1.96d * 20d))
                > committeeFloor);
    }

    [Fact]
    public void AWiderIntervalIsTrustedLessForTheSameMean()
    {
        double tight = MeasuredEdgeConfidence.From(20d, 20d - (1.96d * 2d));
        double loose = MeasuredEdgeConfidence.From(20d, 20d - (1.96d * 40d));

        Assert.True(tight > loose);
    }

    [Fact]
    public void TheAnswerIsAlwaysAProbability()
    {
        foreach (double mean in new[] { -1_000d, -10d, 0d, 10d, 1_000d })
        {
            double value = MeasuredEdgeConfidence.From(mean, mean - 30d);
            Assert.InRange(value, 0d, 1d);
        }
    }

    [Fact]
    public void ADegenerateIntervalAnswersOnTheSignRatherThanDividingByZero()
    {
        // No honest measurement puts its lower bound at or above its mean, but a malformed record
        // must not produce an infinity that then weights a vote.
        Assert.Equal(1d, MeasuredEdgeConfidence.From(10d, 10d));
        Assert.Equal(0d, MeasuredEdgeConfidence.From(-10d, -10d));
    }

    [Fact]
    public void AnUnreadableRecordFallsBackRatherThanPropagatingNaN()
    {
        Assert.Equal(
            ForecastCalibration.Unmeasured, MeasuredEdgeConfidence.From(double.NaN, -10d));
        Assert.Equal(
            ForecastCalibration.Unmeasured,
            MeasuredEdgeConfidence.From(10d, double.NegativeInfinity));
    }

    [Theory]
    [InlineData(0d, 0.5d)]
    [InlineData(1.0d, 0.8413d)]
    [InlineData(-1.0d, 0.1587d)]
    [InlineData(1.96d, 0.9750d)]
    [InlineData(2.5758d, 0.9950d)]
    public void TheNormalCdfMatchesTheTableItStandsIn(double z, double expected)
    {
        // The approximation is accurate to about 1.5e-7, which is far finer than a measured edge
        // supports -- but it has to actually be the normal CDF, or the floor means nothing.
        Assert.Equal(expected, MeasuredEdgeConfidence.NormalCdf(z), precision: 4);
    }
}
