using QuantDesk.Domain.Forecasts;
using QuantDesk.Domain.Scoring;

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
    /// Deliberately at the committee's floor rather than above it. An unmeasured expert is neither
    /// trusted nor refused: it passes while nothing is known and is refused the moment a
    /// measurement says it should be. Starting higher would grant confidence no one has earned.
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
    /// <summary>The measured calibration for an expert and family, or the unmeasured default.</summary>
    double For(int expertId, ForecastType family);
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
    private Dictionary<(int Expert, ForecastType Family), double> _calibration = [];

    /// <summary>Adopts a fresh set of scores.</summary>
    public void Refresh(IReadOnlyList<ExpertForecastScore> scores)
    {
        ArgumentNullException.ThrowIfNull(scores);

        var measured = new Dictionary<(int, ForecastType), double>();
        foreach (ExpertForecastScore score in scores)
        {
            // Scores arrive per regime as well as per expert and family. The worst regime is the
            // one that matters: an expert well calibrated in calm and hopeless in stress is not
            // half-calibrated, it is an expert that fails when it is needed.
            double value = ForecastCalibration.From(score);
            (int, ForecastType) key = (score.ExpertId, score.ForecastType);
            measured[key] = measured.TryGetValue(key, out double existing)
                ? Math.Min(existing, value)
                : value;
        }

        lock (_gate) _calibration = measured;
    }

    public double For(int expertId, ForecastType family)
    {
        lock (_gate)
        {
            return _calibration.TryGetValue((expertId, family), out double value)
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
