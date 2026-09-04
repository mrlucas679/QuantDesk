using QuantDesk.Domain.Numerics;
using QuantDesk.Domain.Scoring;

namespace QuantDesk.Runtime.Scoring;

/// <summary>
/// Splits an episode's result into the parts that explain it, and names what is left over.
///
/// Why a residual is the point
/// ---------------------------
/// Section 17.3 asks five questions of every episode: was the forecast right, was the expression of
/// it appropriate, what did execution cost, was the sizing sensible, and what remains unexplained.
/// The fifth is the one that makes the other four honest. An attribution that always adds up has
/// not explained anything -- it has distributed the answer across whatever buckets were available,
/// and a bucket that absorbs the remainder can hide a systematic error indefinitely.
///
/// So the residual here is computed, not balanced. Every contribution is supplied by whoever
/// measured it, they are summed, and the difference from what the account actually did is reported
/// as residual. A large residual means the decomposition is wrong and should be read as a defect in
/// this system rather than as a property of the market.
///
/// Realism-adjusted, separately
/// ----------------------------
/// Section 14.2 requires broker paper P&amp;L to be kept apart from a realism-adjusted figure. Paper
/// fills are optimistic in ways a live book is not, so the two are carried side by side and never
/// merged: the paper number proves the system behaved, the adjusted number is the only one that
/// says anything about money.
/// </summary>
public static class EpisodeAttributionScorer
{
    /// <summary>
    /// Residual above which the decomposition should not be trusted, as a share of the episode.
    ///
    /// A fifth. Beyond that the parts explain less than they omit, and the right response is to
    /// find the missing term rather than to read the ones that are present.
    /// </summary>
    public const decimal MaximumTrustedResidualShare = 0.20m;

    public static EpisodeAttributionScore Score(EpisodeAttributionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Execution is the sum of what the venue and the book took, kept as one dimension because
        // that is how section 17.3 asks the question -- how much edge was lost getting in and out --
        // while the components stay separable in the input for anyone who needs them apart.
        Usd execution = new(
            -(input.SpreadCost.Value + input.SlippageCost.Value + input.FeeCost.Value));

        Usd timing = new(-input.TimingCost.Value);

        decimal explained =
            input.AlphaOrForecastContribution.Value
            + input.StrategyExpressionContribution.Value
            + execution.Value
            + timing.Value
            + input.SizingRiskContribution.Value
            + input.FactorStyleContribution.Value
            + input.TailRiskContribution.Value
            + input.CrowdingContribution.Value;

        // Computed, never balanced. A residual that is forced to zero has explained nothing.
        Usd residual = new(input.PaperPnl.Value - explained);

        // Paper P&L less the costs a paper fill did not charge. Kept beside the paper figure rather
        // than replacing it, because the two answer different questions.
        Usd realismAdjusted = new(input.PaperPnl.Value - input.AdditionalRealismCost.Value);

        return new EpisodeAttributionScore(
            input.EpisodeId,
            input.PaperPnl,
            realismAdjusted,
            input.AlphaOrForecastContribution,
            input.StrategyExpressionContribution,
            execution,
            timing,
            input.SizingRiskContribution,
            input.FactorStyleContribution,
            input.TailRiskContribution,
            input.CrowdingContribution,
            residual);
    }

    /// <summary>
    /// Whether the parts explain enough of the whole to be worth reading.
    ///
    /// A decomposition whose residual dominates is not a decomposition. Reporting it as though the
    /// named contributions meant something would be the same error as crediting every expert with
    /// the trade's profit: a number in the right shape, describing nothing.
    /// </summary>
    public static bool IsTrustworthy(EpisodeAttributionScore score)
    {
        ArgumentNullException.ThrowIfNull(score);

        decimal magnitude = Math.Abs(score.PaperPnl.Value);

        // An episode that made nothing has no share for the residual to be a fraction of. It is
        // trustworthy exactly when the residual is also nothing.
        if (magnitude == 0m) return score.Residual.Value == 0m;

        return Math.Abs(score.Residual.Value) / magnitude <= MaximumTrustedResidualShare;
    }
}
