using QuantDesk.Domain.Experts;
using QuantDesk.Domain.Forecasts;
using QuantDesk.Domain.Numerics;
using QuantDesk.Runtime.Experts;

namespace QuantDesk.Runtime.Tests.Experts;

public sealed class ExpertCommitteeTests
{
    [Fact]
    public void RequiresFreshCausalConsensus()
    {
        var metadata = new ForecastMetadata(1, 2, ForecastType.DirectionalReturn, TimeSpan.FromMinutes(5), 1, 10, 20, 3, 1, ForecastStatus.Valid);
        var forecast = new DirectionalForecast(metadata, 12, 1, new Probability(0.7), new Probability(0.2), new Probability(0.1), 0.9);
        var result = new ExpertCommittee(0.8, 5).Evaluate(2, [new ExpertVote(1, forecast, 1)], 15, 3);
        Assert.True(result.Actionable);
        Assert.Equal("consensus", result.ReasonCode);
    }

    [Fact]
    public void AbstainsOnStaleEvidence()
    {
        var metadata = new ForecastMetadata(1, 2, ForecastType.DirectionalReturn, TimeSpan.FromMinutes(5), 1, 10, 11, 3, 1, ForecastStatus.Valid);
        var forecast = new DirectionalForecast(metadata, 12, 1, new Probability(0.7), new Probability(0.2), new Probability(0.1), 1);
        var result = new ExpertCommittee(0.8, 5).Evaluate(2, [new ExpertVote(1, forecast, 1)], 12, 3);
        Assert.False(result.Actionable);
        Assert.Equal("insufficient_valid_evidence", result.ReasonCode);
    }

    [Fact]
    public void ReturnsTypedUncertainInsteadOfAveragingContradictoryMechanisms()
    {
        var positive = new DirectionalForecast(
            new ForecastMetadata(1, 2, ForecastType.DirectionalReturn, TimeSpan.FromMinutes(5), 1, 10, 20, 3, 1, ForecastStatus.Valid),
            20, 1, new Probability(.7), new Probability(.2), new Probability(.1), .9);
        var negative = new DirectionalForecast(
            new ForecastMetadata(2, 2, ForecastType.DirectionalReturn, TimeSpan.FromMinutes(5), 1, 10, 20, 3, 1, ForecastStatus.Valid),
            -15, 1, new Probability(.1), new Probability(.2), new Probability(.7), .9);

        CommitteeDecision result = new ExpertCommittee(.8, 5).Evaluate(
            2, [new ExpertVote(1, positive, 1), new ExpertVote(2, negative, 1)], 15, 3);

        Assert.False(result.Actionable);
        Assert.Equal("mechanism_conflict", result.ReasonCode);
        Assert.Equal(CommitteeVerdict.Uncertain, result.Verdict);
    }

    [Fact]
    public void AConfidentBearishCommitteeIsActionable()
    {
        // The last long-only assumption in the decision path. The floor asks whether the expected
        // move is big enough to be worth a round trip, which is a question about size -- but it was
        // asked of the signed return, so a committee expecting -51 bps failed a +1 bps floor by
        // construction and was reported as disagreement. The experts had not disagreed; they agreed
        // emphatically that the price was going down.
        //
        // Live on 2026-09-04: three equity rules fired short on SPY, QQQ and IWM with a measured
        // record behind them, and every one died here.
        CommitteeDecision result = new ExpertCommittee(0.6, 1).Evaluate(
            2, [Vote(1, expectedReturnBps: -51.4, calibration: 0.99, weight: 0.99)], 15, 3);

        Assert.True(result.Actionable, result.ReasonCode);
        Assert.Equal(-51.4, result.ExpectedReturnBps, precision: 6);
    }

    [Fact]
    public void AMoveTooSmallToPayForItselfIsStillRefusedInBothDirections()
    {
        // The floor still does its job. Taking the magnitude must not turn "too small to be worth
        // trading" into "large enough because it is negative".
        var committee = new ExpertCommittee(0.6, 5);

        Assert.False(committee.Evaluate(
            2, [Vote(1, expectedReturnBps: -2d, calibration: 0.99, weight: 0.99)], 15, 3).Actionable);
        Assert.False(committee.Evaluate(
            2, [Vote(1, expectedReturnBps: 2d, calibration: 0.99, weight: 0.99)], 15, 3).Actionable);
    }

    [Fact]
    public void ExpertsPullingOppositeWaysAreStillUncertainRatherThanRescuedByAnAbsoluteValue()
    {
        // Mechanism conflict is refused before the floor is reached, so a near-zero average made of
        // two opposing forecasts cannot be turned into a large one by taking its magnitude.
        CommitteeDecision result = new ExpertCommittee(0.6, 1).Evaluate(
            2,
            [
                Vote(1, expectedReturnBps: 40d, calibration: 0.99, weight: 0.99),
                Vote(2, expectedReturnBps: -40d, calibration: 0.99, weight: 0.99),
            ],
            15,
            3);

        Assert.False(result.Actionable);
        Assert.Equal("mechanism_conflict", result.ReasonCode);
    }

    /// <summary>One vote, valid at tick 15 against state version 3.</summary>
    private static ExpertVote Vote(
        int expertId, double expectedReturnBps, double calibration, double weight)
    {
        Assert.True(Probability.TryCreate(0.6, out Probability up));
        Assert.True(Probability.TryCreate(0.2, out Probability neutral));
        Assert.True(Probability.TryCreate(0.2, out Probability down));

        return new ExpertVote(
            expertId,
            new DirectionalForecast(
                new ForecastMetadata(
                    expertId, 2, ForecastType.DirectionalReturn, TimeSpan.FromMinutes(5),
                    10, 12, 100, 3, 1, ForecastStatus.Valid),
                expectedReturnBps, 1, up, neutral, down, calibration),
            weight);
    }
}
