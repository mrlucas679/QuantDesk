using QuantDesk.Domain.Forecasts;
using QuantDesk.Domain.Scoring;

namespace QuantDesk.Runtime.Scoring;

/// <summary>
/// Scores each expert against its own forecast, not against the trade's profit.
///
/// The mistake this exists to prevent
/// ----------------------------------
/// Section 17.4 states it plainly: do not credit every expert with the same trade P&amp;L. It is the
/// easiest attribution to write and it is worthless. Where four experts publish and one position
/// results, all four receive the same number, so an expert that was right about volatility is
/// punished for a directional call it never made, and one that was wrong inside a profitable trade
/// is rewarded for someone else's work. After enough of that the weights encode which experts
/// happened to be present rather than which were right.
///
/// So each family is scored on its own quantity with its own rule. A directional forecast is right
/// or wrong about a return; a volatility forecast is right or wrong about a variance; a probability
/// is right or wrong about an event. These are not comparable and are never averaged into one
/// number.
///
/// Why the metric differs by family
/// --------------------------------
/// QLIKE for variance rather than squared error, because squared error on a variance is dominated
/// by the few largest observations and quietly rewards a model that forecasts high. QLIKE is
/// scale-invariant and punishes under-forecasting harder than over-forecasting, which is the
/// asymmetry a risk system actually wants. Brier for probabilities because it is proper: it is
/// minimised by telling the truth, so an expert cannot improve its score by shading confidence in
/// either direction. Root mean squared error for returns, which is what the research plane already
/// reports, so the two can be read against each other.
///
/// Independent episodes, not observations
/// --------------------------------------
/// A rule firing ten times in one hour on one market move has made one forecast about one thing,
/// ten times. Counting that as ten observations is how a sample of hundreds turns out to hold a
/// handful of independent bets, which is the same error the correlation work found in the
/// portfolio. Both counts are reported and the evidence bar is set against the independent one.
/// </summary>
public static class ExpertForecastScorer
{
    /// <summary>
    /// Independent episodes required before a score is published rather than withheld.
    ///
    /// Twelve, matching the bar the research scan and the shadow ledger already use, so a score
    /// here and a figure there are read on the same terms.
    /// </summary>
    public const int MinimumIndependentEpisodes = 12;

    public static IReadOnlyList<ExpertForecastScore> Score(
        IReadOnlyList<ExpertForecastOutcome> outcomes)
    {
        ArgumentNullException.ThrowIfNull(outcomes);

        List<ExpertForecastScore> scores = [];
        foreach (IGrouping<(int Expert, ForecastType Type, string Regime), ExpertForecastOutcome> group
            in outcomes
                .Where(outcome => outcome is not null && outcome.IsValid())
                .GroupBy(outcome => (outcome.ExpertId, outcome.ForecastType, outcome.Regime)))
        {
            scores.Add(ScoreGroup(group.Key.Expert, group.Key.Type, group.Key.Regime, [.. group]));
        }

        return scores;
    }

    private static ExpertForecastScore ScoreGroup(
        int expertId,
        ForecastType type,
        string regime,
        IReadOnlyList<ExpertForecastOutcome> outcomes)
    {
        int episodes = outcomes.Select(outcome => outcome.EpisodeId).Distinct().Count();
        ForecastScoreMetric metric = MetricFor(type);

        if (episodes < MinimumIndependentEpisodes)
        {
            // Withheld rather than reported wide. A score from four episodes is not a noisy score,
            // it is a statement about four moments, and publishing it invites it to be read as a
            // statement about the expert.
            return new ExpertForecastScore(
                expertId, type, regime, metric, ScoreEvidenceStatus.InsufficientEvidence,
                outcomes.Count, episodes,
                null, null, null, null, null, null, null);
        }

        double absoluteError = 0d;
        double squaredError = 0d;
        int directionalHits = 0;
        int directionalCounted = 0;

        foreach (ExpertForecastOutcome outcome in outcomes)
        {
            double error = outcome.PredictedValue - outcome.ObservedValue;
            absoluteError += Math.Abs(error);
            squaredError += error * error;

            // A forecast of exactly zero expresses no direction, so scoring it as a directional
            // call would be scoring an abstention.
            if (outcome.PredictedValue == 0d || outcome.ObservedValue == 0d) continue;
            directionalCounted++;
            if (Math.Sign(outcome.PredictedValue) == Math.Sign(outcome.ObservedValue)) directionalHits++;
        }

        double mae = absoluteError / outcomes.Count;
        double rmse = Math.Sqrt(squaredError / outcomes.Count);
        double? brier = Brier(outcomes);
        double? qlike = QLike(outcomes);
        double? accuracy = directionalCounted > 0 ? (double)directionalHits / directionalCounted : null;

        double? primary = metric switch
        {
            ForecastScoreMetric.QLike => qlike,
            ForecastScoreMetric.Brier => brier,
            _ => rmse,
        };

        return new ExpertForecastScore(
            expertId, type, regime, metric,
            primary is null ? ScoreEvidenceStatus.InsufficientEvidence : ScoreEvidenceStatus.Scored,
            outcomes.Count, episodes,
            primary, mae, rmse, brier, qlike, accuracy, CalibrationError(outcomes));
    }

    /// <summary>The rule each family is judged by, and the reason it is not one rule.</summary>
    private static ForecastScoreMetric MetricFor(ForecastType type) => type switch
    {
        ForecastType.RealizedVolatility => ForecastScoreMetric.QLike,
        ForecastType.Regime or ForecastType.JumpRisk => ForecastScoreMetric.Brier,
        _ => ForecastScoreMetric.RootMeanSquaredError,
    };

    /// <summary>
    /// QLIKE loss over variance forecasts: observed/predicted minus its log, less one.
    ///
    /// Scale-invariant, so a quiet instrument and a violent one contribute comparably instead of
    /// the violent one deciding the score, and asymmetric in the direction risk cares about:
    /// under-forecasting variance is punished harder than over-forecasting it.
    /// </summary>
    private static double? QLike(IReadOnlyList<ExpertForecastOutcome> outcomes)
    {
        double total = 0d;
        int counted = 0;

        foreach (ExpertForecastOutcome outcome in outcomes)
        {
            // A non-positive variance is not a variance. Skipping stops one bad record producing
            // an infinite loss that then defines the expert.
            if (outcome.PredictedValue <= 0d || outcome.ObservedValue <= 0d) continue;

            double ratio = outcome.ObservedValue / outcome.PredictedValue;
            double loss = ratio - Math.Log(ratio) - 1d;
            if (!double.IsFinite(loss)) continue;

            total += loss;
            counted++;
        }

        return counted > 0 ? total / counted : null;
    }

    /// <summary>
    /// Brier score over probabilistic forecasts: mean squared distance from what happened.
    ///
    /// Proper, which is the whole reason to use it. It is minimised by reporting the true
    /// probability, so an expert cannot improve its score by shading its confidence.
    /// </summary>
    private static double? Brier(IReadOnlyList<ExpertForecastOutcome> outcomes)
    {
        double total = 0d;
        int counted = 0;

        foreach (ExpertForecastOutcome outcome in outcomes)
        {
            if (outcome.PredictedProbability is not { } probability) continue;
            if (outcome.EventOccurred is not { } occurred) continue;

            double error = probability - (occurred ? 1d : 0d);
            total += error * error;
            counted++;
        }

        return counted > 0 ? total / counted : null;
    }

    /// <summary>
    /// How far the average stated probability sat from the observed frequency.
    ///
    /// Separate from Brier because they answer different questions. Brier asks whether the
    /// forecasts were sharp and true together; this asks only whether the expert is systematically
    /// over- or under-confident, which is the part a committee weight should react to.
    /// </summary>
    private static double? CalibrationError(IReadOnlyList<ExpertForecastOutcome> outcomes)
    {
        double predicted = 0d;
        double observed = 0d;
        int counted = 0;

        foreach (ExpertForecastOutcome outcome in outcomes)
        {
            if (outcome.PredictedProbability is not { } probability) continue;
            if (outcome.EventOccurred is not { } occurred) continue;

            predicted += probability;
            observed += occurred ? 1d : 0d;
            counted++;
        }

        return counted > 0 ? Math.Abs((predicted - observed) / counted) : null;
    }
}
