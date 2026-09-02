using QuantDesk.Domain.Numerics;
using QuantDesk.Domain.Scoring;
using QuantDesk.Runtime.Scoring;

namespace QuantDesk.Runtime.Tests.Scoring;

/// <summary>
/// Splitting an episode into the parts that explain it, and naming what is left.
///
/// The residual is the point. An attribution that always adds up has not explained anything -- it
/// has distributed the answer across whatever buckets were available, and a bucket that absorbs the
/// remainder can hide a systematic error indefinitely.
/// </summary>
public sealed class EpisodeAttributionScorerTests
{
    [Fact]
    public void TheResidualIsComputedRatherThanBalanced()
    {
        // Contributions explaining 6 of a 10 result leave 4 unexplained, and it must say so rather
        // than quietly widening one of the named parts to make the arithmetic close.
        EpisodeAttributionScore score = EpisodeAttributionScorer.Score(Input(
            paperPnl: 10m, forecast: 6m));

        Assert.Equal(4m, score.Residual.Value);
    }

    [Fact]
    public void AFullyExplainedEpisodeHasNoResidual()
    {
        EpisodeAttributionScore score = EpisodeAttributionScorer.Score(Input(
            paperPnl: 10m, forecast: 12m, spread: 1m, slippage: 0.5m, fee: 0.5m));

        Assert.Equal(0m, score.Residual.Value);
    }

    [Fact]
    public void ExecutionGathersWhatTheVenueAndTheBookTookAndIsNegative()
    {
        // Section 17.3 asks execution as one question -- how much edge was lost getting in and out
        // -- while the components stay separable in the input for anyone who needs them apart.
        EpisodeAttributionScore score = EpisodeAttributionScorer.Score(Input(
            paperPnl: 0m, spread: 1m, slippage: 2m, fee: 3m));

        Assert.Equal(-6m, score.ExecutionContribution.Value);
    }

    [Fact]
    public void RealismAdjustedPnlIsCarriedBesideThePaperFigureNotInsteadOfIt()
    {
        // Section 14.2. Paper fills are optimistic in ways a live book is not: the paper number
        // proves the system behaved, the adjusted number is the only one that says anything about
        // money, and merging them would lose both claims.
        EpisodeAttributionScore score = EpisodeAttributionScorer.Score(Input(
            paperPnl: 10m, realismCost: 3m));

        Assert.Equal(10m, score.PaperPnl.Value);
        Assert.Equal(7m, score.RealismAdjustedPnl.Value);
    }

    [Fact]
    public void ADecompositionDominatedByItsResidualIsNotTrustworthy()
    {
        // Reporting the named contributions as meaningful when they explain less than they omit
        // would be the same error as crediting every expert with the trade's profit: a number in
        // the right shape, describing nothing.
        Assert.False(EpisodeAttributionScorer.IsTrustworthy(
            EpisodeAttributionScorer.Score(Input(paperPnl: 100m, forecast: 10m))));
    }

    [Fact]
    public void ADecompositionThatExplainsMostOfTheEpisodeIsTrustworthy()
    {
        Assert.True(EpisodeAttributionScorer.IsTrustworthy(
            EpisodeAttributionScorer.Score(Input(paperPnl: 100m, forecast: 95m))));
    }

    [Fact]
    public void AnEpisodeThatMadeNothingIsTrustworthyOnlyWhenNothingIsUnexplained()
    {
        // No magnitude for the residual to be a share of, so the test has to be exact rather than
        // proportional -- otherwise a flat episode with unexplained parts would pass by dividing
        // by zero.
        Assert.True(EpisodeAttributionScorer.IsTrustworthy(
            EpisodeAttributionScorer.Score(Input(paperPnl: 0m))));

        Assert.False(EpisodeAttributionScorer.IsTrustworthy(
            EpisodeAttributionScorer.Score(Input(paperPnl: 0m, forecast: 5m))));
    }

    [Fact]
    public void EveryDimensionSurvivesIntoTheScore()
    {
        // Five questions in section 17.3, and each has to be answerable from the result rather
        // than folded into a neighbour.
        EpisodeAttributionScore score = EpisodeAttributionScorer.Score(Input(
            paperPnl: 10m, forecast: 4m, expression: 2m, spread: 1m,
            sizing: 1m, factor: 0.5m, tail: -0.5m, crowding: 0.25m, timing: 0.75m));

        Assert.Equal(4m, score.ForecastContribution.Value);
        Assert.Equal(2m, score.StrategyExpressionContribution.Value);
        Assert.Equal(-1m, score.ExecutionContribution.Value);
        Assert.Equal(-0.75m, score.TimingContribution.Value);
        Assert.Equal(1m, score.RiskSizingContribution.Value);
        Assert.Equal(0.5m, score.FactorStyleContribution.Value);
        Assert.Equal(-0.5m, score.TailRiskContribution.Value);
        Assert.Equal(0.25m, score.CrowdingContribution.Value);
    }

    private static EpisodeAttributionInput Input(
        decimal paperPnl,
        decimal forecast = 0m,
        decimal expression = 0m,
        decimal spread = 0m,
        decimal slippage = 0m,
        decimal fee = 0m,
        decimal timing = 0m,
        decimal sizing = 0m,
        decimal factor = 0m,
        decimal tail = 0m,
        decimal crowding = 0m,
        decimal realismCost = 0m) =>
        new(
            EpisodeId: 1,
            PaperPnl: new Usd(paperPnl),
            AlphaOrForecastContribution: new Usd(forecast),
            StrategyExpressionContribution: new Usd(expression),
            SpreadCost: new Usd(spread),
            SlippageCost: new Usd(slippage),
            FeeCost: new Usd(fee),
            TimingCost: new Usd(timing),
            SizingRiskContribution: new Usd(sizing),
            FactorStyleContribution: new Usd(factor),
            TailRiskContribution: new Usd(tail),
            CrowdingContribution: new Usd(crowding),
            AdditionalRealismCost: new Usd(realismCost));
}
