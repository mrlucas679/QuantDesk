using QuantDesk.Domain.Forecasts;
using QuantDesk.Domain.Scoring;
using QuantDesk.Runtime.Scoring;

namespace QuantDesk.Runtime.Tests.Scoring;

/// <summary>
/// Scoring each expert on its own forecast rather than on the trade's profit.
///
/// Section 17.4 forbids crediting every expert with the same P&amp;L, and the reason is not
/// fastidiousness: where four experts publish and one position results, a shared number punishes an
/// expert that was right about volatility for a directional call it never made, and rewards one
/// that was wrong inside a profitable trade. After enough of that the weights encode which experts
/// were present rather than which were right.
/// </summary>
public sealed class ExpertForecastScorerTests
{
    [Fact]
    public void EachFamilyIsJudgedByItsOwnRule()
    {
        // Not comparable quantities, so not one metric. A variance forecast being "off by 0.1"
        // means something entirely different from a return forecast being off by 0.1.
        Assert.Equal(
            ForecastScoreMetric.RootMeanSquaredError,
            Score(ForecastType.DirectionalReturn)[0].PrimaryMetric);
        Assert.Equal(
            ForecastScoreMetric.QLike,
            Score(ForecastType.RealizedVolatility, predicted: 4d, observed: 4d)[0].PrimaryMetric);
        Assert.Equal(
            ForecastScoreMetric.Brier,
            Score(ForecastType.Regime, probability: 0.7d, occurred: true)[0].PrimaryMetric);
    }

    [Fact]
    public void TwoExpertsOnTheSameEpisodesAreScoredDifferentlyWhenTheyForecastDifferently()
    {
        // The whole point. Same episodes, same market, different forecasts, different scores.
        List<ExpertForecastOutcome> outcomes =
        [
            .. Enumerable.Range(1, 12).Select(i => Outcome(i, expertId: 1, predicted: 10d, observed: 10d)),
            .. Enumerable.Range(1, 12).Select(i => Outcome(i, expertId: 2, predicted: 90d, observed: 10d)),
        ];

        IReadOnlyList<ExpertForecastScore> scores = ExpertForecastScorer.Score(outcomes);

        double accurate = scores.Single(s => s.ExpertId == 1).RootMeanSquaredError!.Value;
        double wrong = scores.Single(s => s.ExpertId == 2).RootMeanSquaredError!.Value;

        Assert.True(accurate < wrong);
        Assert.Equal(0d, accurate, precision: 9);
    }

    [Fact]
    public void RepeatedForecastsOnOneEpisodeCountAsOneEpisode()
    {
        // A rule firing ten times on one market move made one forecast about one thing, ten times.
        // Counting it as ten is how a sample of hundreds turns out to hold a handful of bets.
        IReadOnlyList<ExpertForecastOutcome> outcomes =
            [.. Enumerable.Range(0, 40).Select(_ => Outcome(episodeId: 1, expertId: 1, 10d, 10d))];

        ExpertForecastScore score = ExpertForecastScorer.Score(outcomes)[0];

        Assert.Equal(40, score.SampleCount);
        Assert.Equal(1, score.IndependentEpisodeCount);
        Assert.Equal(ScoreEvidenceStatus.InsufficientEvidence, score.Status);
    }

    [Fact]
    public void AScoreIsWithheldBelowTheEvidenceBarRatherThanReportedWide()
    {
        // A score from four episodes is not a noisy score, it is a statement about four moments.
        ExpertForecastScore score = ExpertForecastScorer.Score(
            [.. Enumerable.Range(1, 4).Select(i => Outcome(i, 1, 10d, 10d))])[0];

        Assert.Equal(ScoreEvidenceStatus.InsufficientEvidence, score.Status);
        Assert.Null(score.PrimaryLoss);
        Assert.Null(score.RootMeanSquaredError);
    }

    [Fact]
    public void QLikePunishesUnderForecastingVarianceHarderThanOverForecasting()
    {
        // The asymmetry a risk system wants. Being told the world is calmer than it is costs more
        // than being told it is wilder.
        double under = Score(ForecastType.RealizedVolatility, predicted: 1d, observed: 4d)[0].QLike!.Value;
        double over = Score(ForecastType.RealizedVolatility, predicted: 4d, observed: 1d)[0].QLike!.Value;

        Assert.True(under > over);
    }

    [Fact]
    public void QLikeIsScaleInvariant()
    {
        // A quiet instrument and a violent one must contribute comparably, or the violent one
        // decides the score for reasons that have nothing to do with forecast quality.
        double small = Score(ForecastType.RealizedVolatility, predicted: 1d, observed: 2d)[0].QLike!.Value;
        double large = Score(ForecastType.RealizedVolatility, predicted: 1_000d, observed: 2_000d)[0].QLike!.Value;

        Assert.Equal(small, large, precision: 9);
    }

    [Fact]
    public void APerfectVarianceForecastScoresZeroQLike()
    {
        Assert.Equal(
            0d, Score(ForecastType.RealizedVolatility, predicted: 4d, observed: 4d)[0].QLike!.Value,
            precision: 9);
    }

    [Fact]
    public void BrierRewardsTellingTheTruthRatherThanSoundingConfident()
    {
        // Proper scoring: an expert cannot improve its score by shading confidence. Across twelve
        // episodes where the event happens two thirds of the time, saying two thirds beats saying
        // it is certain.
        IReadOnlyList<ExpertForecastOutcome> honest =
        [
            .. Enumerable.Range(1, 12).Select(i =>
                Outcome(i, 1, 0d, 0d, ForecastType.Regime, probability: 2d / 3d, occurred: i % 3 != 0)),
        ];
        IReadOnlyList<ExpertForecastOutcome> overconfident =
        [
            .. Enumerable.Range(1, 12).Select(i =>
                Outcome(i, 1, 0d, 0d, ForecastType.Regime, probability: 1d, occurred: i % 3 != 0)),
        ];

        Assert.True(
            ExpertForecastScorer.Score(honest)[0].BrierScore!.Value
            < ExpertForecastScorer.Score(overconfident)[0].BrierScore!.Value);
    }

    [Fact]
    public void ANonPositiveVarianceIsSkippedRatherThanProducingAnInfiniteLoss()
    {
        // One bad record must not define the expert.
        List<ExpertForecastOutcome> outcomes =
            [.. Enumerable.Range(1, 12).Select(i => Outcome(i, 1, 4d, 4d, ForecastType.RealizedVolatility))];
        outcomes.Add(Outcome(13, 1, 0d, 4d, ForecastType.RealizedVolatility));

        double qlike = ExpertForecastScorer.Score(outcomes)[0].QLike!.Value;

        Assert.True(double.IsFinite(qlike));
        Assert.Equal(0d, qlike, precision: 9);
    }

    [Fact]
    public void ScoresAreSeparatedByRegimeSoContextFitCanBeRead()
    {
        // Section 12.2 scores experts on context fit, which needs the context kept separate. An
        // expert that is excellent in a range and useless in stress must not average to mediocre.
        List<ExpertForecastOutcome> outcomes =
        [
            .. Enumerable.Range(1, 12).Select(i => Outcome(i, 1, 10d, 10d, regime: "Range")),
            .. Enumerable.Range(20, 12).Select(i => Outcome(i, 1, 10d, 90d, regime: "Stress")),
        ];

        IReadOnlyList<ExpertForecastScore> scores = ExpertForecastScorer.Score(outcomes);

        Assert.Equal(2, scores.Count);
        Assert.True(
            scores.Single(s => s.Regime == "Range").RootMeanSquaredError
            < scores.Single(s => s.Regime == "Stress").RootMeanSquaredError);
    }

    [Fact]
    public void DirectionalAccuracyIgnoresForecastsThatExpressedNoDirection()
    {
        // A forecast of exactly zero is an abstention, and scoring it as a directional call would
        // score the expert for declining to make one.
        List<ExpertForecastOutcome> outcomes =
        [
            .. Enumerable.Range(1, 12).Select(i => Outcome(i, 1, 10d, 10d)),
            .. Enumerable.Range(20, 6).Select(i => Outcome(i, 1, 0d, 10d)),
        ];

        Assert.Equal(1d, ExpertForecastScorer.Score(outcomes)[0].DirectionalAccuracy!.Value, precision: 9);
    }

    [Fact]
    public void AnInvalidOutcomeIsExcludedRatherThanScored()
    {
        List<ExpertForecastOutcome> outcomes =
            [.. Enumerable.Range(1, 12).Select(i => Outcome(i, 1, 10d, 10d))];
        outcomes.Add(Outcome(99, 1, double.NaN, 10d));

        Assert.Equal(12, ExpertForecastScorer.Score(outcomes)[0].SampleCount);
    }

    private static IReadOnlyList<ExpertForecastScore> Score(
        ForecastType type,
        double predicted = 10d,
        double observed = 10d,
        double? probability = null,
        bool? occurred = null) =>
        ExpertForecastScorer.Score(
        [
            .. Enumerable.Range(1, 12).Select(i =>
                Outcome(i, 1, predicted, observed, type, probability: probability, occurred: occurred)),
        ]);

    private static ExpertForecastOutcome Outcome(
        int episodeId,
        int expertId,
        double predicted,
        double observed,
        ForecastType type = ForecastType.DirectionalReturn,
        string regime = "Range",
        double? probability = null,
        bool? occurred = null) =>
        new(episodeId, episodeId * 100L + expertId, expertId, type, predicted, observed,
            probability, occurred, regime);
}
