using QuantDesk.Domain.Forecasts;
using QuantDesk.Domain.Scoring;
using QuantDesk.Domain.Trading;

namespace QuantDesk.Runtime.Scoring;

/// <summary>
/// Turns a measured forecast loss into the calibration score the committee gates on.
///
/// What this replaces
/// ------------------
/// Every expert reported a constant 0.5, against a committee floor of 0.5 -- so the gate passed by
/// a hair's breadth and could never refuse anything. The scorer has been measuring QLIKE and Brier
/// against realised outcomes the whole time, and nothing read the result.
///
/// The mapping, and why this one
/// -----------------------------
/// A calibration score has to be bounded, one at a perfect forecast, monotonically decreasing in
/// loss, and -- most importantly -- its threshold has to mean something a person can argue with.
///
/// exp(-loss) satisfies the first three. The fourth is what makes it defensible: for QLIKE, which
/// is r - ln(r) - 1 where r is the ratio of realised to forecast variance, a score of 0.5
/// corresponds to a loss of ln(2) = 0.693, and solving r - ln(r) - 1 = 0.693 gives r = 2.6 or
/// r = 0.22. So the default floor says, exactly: a variance forecast typically wrong by a factor of
/// about two and a half stops being worth sizing on.
///
/// That number is arguable, which is the point. It is stated here rather than buried, and moving
/// the floor moves a quantity with a meaning rather than a dial.
///
/// The alternative considered was skill against a naive baseline, as the LightGBM held-out gate
/// uses -- one minus the ratio of the expert's loss to a random-walk forecast's. It is the better
/// measure and needs a baseline nobody is computing yet; when one exists this should become it.
///
/// Why a badly calibrated expert still gets scored
/// -----------------------------------------------
/// This gates what informs a decision. It must not gate what gets recorded for scoring, or a
/// expert that scores badly stops producing the evidence that would show it improving and can
/// never earn its way back -- the same trap the shadow signal log exists to avoid for rules.
/// </summary>
public static class ForecastCalibration
{
    /// <summary>What an expert reports before anything has been measured about it.</summary>
    ///
    /// <remarks>
    /// A half. Not, as this comment claimed for a long time, "the committee's floor" -- the floor is
    /// 0.60, so an unmeasured expert sits *below* it and is refused rather than admitted. The claim
    /// went unnoticed because the directional votes carried a hardcoded 0.75 that cleared the floor
    /// on their own, so nothing ever depended on this value being what it said it was.
    ///
    /// A half is still the right number, for a better reason than the one that was written here:
    /// <see cref="MeasuredEdgeConfidence"/> returns exactly this for a measured edge of zero, so an
    /// expert with no record and an expert measured to have no edge weigh the same by construction.
    /// The consequence -- unmeasured means not actionable -- is what "no record, no weight" means,
    /// and it is intended.
    /// </remarks>
    public const double Unmeasured = 0.5d;

    /// <summary>
    /// The calibration implied by a measured score, or <see cref="Unmeasured"/> when there is none.
    /// </summary>
    public static double From(ExpertForecastScore? score)
    {
        if (score is null) return Unmeasured;

        // Withheld below the scorer's independent-episode minimum. A loss computed from a handful
        // of overlapping windows is a number, not evidence, and letting it drive a gate would
        // refuse an expert on the strength of an afternoon.
        if (score.Status is not ScoreEvidenceStatus.Scored) return Unmeasured;
        if (score.PrimaryLoss is not { } loss) return Unmeasured;
        if (!double.IsFinite(loss) || loss < 0d) return Unmeasured;

        return Math.Clamp(Math.Exp(-loss), 0d, 1d);
    }

    /// <summary>
    /// The loss at which calibration falls to <paramref name="calibration"/>.
    ///
    /// The inverse, so a threshold can be read back as the loss it corresponds to rather than
    /// argued about as an opaque fraction.
    /// </summary>
    public static double LossAt(double calibration) =>
        calibration is > 0d and <= 1d ? -Math.Log(calibration) : double.PositiveInfinity;

    /// <summary>
    /// The variance-ratio a QLIKE loss corresponds to, for the reading above the fold.
    ///
    /// QLIKE is r - ln(r) - 1. It has two roots for any positive loss, one above one and one below,
    /// because a forecast twice too large and one half too small are equally wrong. This returns the
    /// larger, which is the one people mean when they ask how far off a forecast is.
    /// </summary>
    public static double VarianceRatioAt(double qlike)
    {
        if (!double.IsFinite(qlike) || qlike <= 0d) return 1d;

        // r - ln(r) - 1 is convex with its minimum at r = 1, so bisection on [1, upper] converges
        // on the upper root. No closed form exists; this is Lambert's W in disguise.
        double lower = 1d;
        double upper = 2d;
        while (upper - Math.Log(upper) - 1d < qlike && upper < 1e12d) upper *= 2d;

        for (int step = 0; step < 200; step++)
        {
            double middle = (lower + upper) / 2d;
            if (middle - Math.Log(middle) - 1d < qlike) lower = middle;
            else upper = middle;
        }

        return (lower + upper) / 2d;
    }
}

/// <summary>What an expert should report as its calibration, by family.</summary>
public interface IForecastCalibrationSource
{
    /// <summary>
    /// The measured calibration for an expert, family and book, or the unmeasured default.
    ///
    /// The book is part of the question. A record measured on continuously-traded crypto says
    /// nothing about an equity ETF with an opening auction and a close, and answering with it
    /// would repeat -- one layer up -- the defect that had one BTC-fitted model forecasting four
    /// equity ETFs.
    /// </summary>
    double For(int expertId, ForecastType family, TradedAssetClass assetClass);
}

/// <summary>
/// Serves calibration from the scores the outcome log has measured.
///
/// Refreshed rather than recomputed per forecast: scoring walks every resolved outcome, and doing
/// that on the decision path would put a file scan inside a forecast.
/// </summary>
public sealed class MeasuredCalibrationSource : IForecastCalibrationSource
{
    private readonly Lock _gate = new();
    private Dictionary<(int Expert, ForecastType Family, TradedAssetClass Book), double>
        _calibration = [];

    /// <summary>Adopts a fresh set of scores.</summary>
    public void Refresh(IReadOnlyList<ExpertForecastScore> scores)
    {
        ArgumentNullException.ThrowIfNull(scores);

        var measured = new Dictionary<(int, ForecastType, TradedAssetClass), double>();
        foreach (ExpertForecastScore score in scores)
        {
            // Scores arrive per regime as well as per expert, family and book. The worst regime is
            // the one that matters: an expert well calibrated in calm and hopeless in stress is not
            // half-calibrated, it is an expert that fails when it is needed.
            //
            // Across regimes, not across books. Taking the minimum over both would let a bad crypto
            // record silently condemn a good equity one, and the resulting number would describe
            // neither book -- which is the same mistake as pooling shadow evidence across two
            // venues whose costs differ by a factor of seven.
            double value = ForecastCalibration.From(score);
            (int, ForecastType, TradedAssetClass) key =
                (score.ExpertId, score.ForecastType, score.AssetClass);
            measured[key] = measured.TryGetValue(key, out double existing)
                ? Math.Min(existing, value)
                : value;
        }

        lock (_gate) _calibration = measured;
    }

    public double For(int expertId, ForecastType family, TradedAssetClass assetClass)
    {
        lock (_gate)
        {
            return _calibration.TryGetValue((expertId, family, assetClass), out double value)
                ? value
                : ForecastCalibration.Unmeasured;
        }
    }

    /// <summary>Everything measured, for the status surface.</summary>
    public IReadOnlyDictionary<string, double> Snapshot()
    {
        lock (_gate)
        {
            return _calibration.ToDictionary(
                entry => $"{entry.Key.Expert}:{entry.Key.Family}",
                entry => entry.Value,
                StringComparer.Ordinal);
        }
    }
}

/// <summary>
/// What a rule's own measured record says its forecast is worth, as a probability.
///
/// The committee weights every vote and averages those weights into an agreement score. Both
/// numbers arrived as literals -- a calibration of 0.75 and a weight of 0.5, on every vote, for
/// every rule, on every instrument -- so the agreement floor was tested against a constant and the
/// measured record reached no decision at all.
///
/// The quantity, and why this one
/// ------------------------------
/// The probability that the rule's true net edge is above zero, estimated from its measured mean
/// and the standard error implied by its published bound. Three properties make it the right
/// scalar rather than one more dial:
///
/// It is a probability, so it is bounded and needs no clamping to be meaningful. It is exactly 0.5
/// when the measured edge is zero, which *derives* <see cref="ForecastCalibration.Unmeasured"/>
/// instead of asserting it -- a rule with no edge and a rule with no record are equally uninformed,
/// and it is right that they weigh the same. And the committee's 0.60 floor becomes readable: it
/// admits a rule whose net edge is about a quarter of a standard error above zero, which is a
/// quantity a person can argue with rather than a fraction to be tuned.
///
/// Net, not gross. A rule earning twenty basis points against a sixty basis point toll has a
/// negative edge, and weighting it on the gross figure would trust it precisely where it loses
/// money. The 2026-09-04 model comparison measured that gap directly across three model families.
///
/// The standard error is inverted from the published two-sided 95% bound, the same way
/// <c>IsKnownToLose</c> and the shadow condemnation test already do it. A third convention for the
/// same quantity is how two numbers that should agree stop agreeing.
/// </summary>
public static class MeasuredEdgeConfidence
{
    /// <summary>
    /// The probability that a measured net edge is truly positive.
    /// </summary>
    /// <param name="meanNetBps">Measured mean net edge, after the venue's real round trip.</param>
    /// <param name="lowerBoundBps">The lower end of its two-sided 95% interval.</param>
    /// <remarks>
    /// A degenerate interval -- a bound at or above the mean, which no honest measurement produces
    /// -- has no error to reason about, so this answers on the sign of the mean alone rather than
    /// dividing by zero: a positive edge measured without spread is believed, a negative one is not.
    /// </remarks>
    public static double From(double meanNetBps, double lowerBoundBps)
    {
        if (!double.IsFinite(meanNetBps) || !double.IsFinite(lowerBoundBps))
            return ForecastCalibration.Unmeasured;

        double standardError = (meanNetBps - lowerBoundBps) / 1.96d;
        if (standardError <= 0d) return meanNetBps > 0d ? 1d : 0d;

        return NormalCdf(meanNetBps / standardError);
    }

    /// <summary>
    /// The standard normal CDF, via the error function's Abramowitz-Stegun 7.1.26 approximation.
    ///
    /// Accurate to about 1.5e-7, which is several orders of magnitude finer than the quantity it is
    /// applied to: a measured edge known to a basis point does not support a probability known to
    /// more than a few decimals, and pulling in a statistics package for this one function would be
    /// a dependency carried for false precision.
    /// </summary>
    public static double NormalCdf(double z)
    {
        if (double.IsNaN(z)) return ForecastCalibration.Unmeasured;

        double sign = z < 0d ? -1d : 1d;
        double x = Math.Abs(z) / Math.Sqrt(2d);
        double t = 1d / (1d + (0.3275911d * x));
        double y = 1d - (((((((((1.061405429d * t) - 1.453152027d) * t) + 1.421413741d) * t)
            - 0.284496736d) * t) + 0.254829592d) * t * Math.Exp(-x * x));

        return 0.5d * (1d + (sign * y));
    }
}
