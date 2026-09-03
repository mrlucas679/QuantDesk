using QuantDesk.Domain.Contracts;

namespace QuantDesk.Runtime.Experts;

/// <summary>
/// HAR variance inference in C#, from coefficients fitted in Python.
///
/// Why this model crosses the boundary and the others do not
/// ---------------------------------------------------------
/// Section 20.3 says it directly: HAR is fit in Python and its coefficient inference can run in C#.
/// That is not an arbitrary split. HAR's inference is a dot product of four numbers -- an intercept
/// and three lag weights -- so porting it means copying four doubles, and the runtime gains a
/// fitted model without gaining a Python dependency on the trading path. An HMM's filtering or a
/// gradient-boosted ensemble's traversal are not four numbers, and pretending otherwise is how a
/// reimplementation quietly diverges from the model it claims to be.
///
/// This was once described here as the whole of the bridge that belongs in C#, with HMM and
/// LightGBM said to need a warm Python worker. That was wrong and is corrected in
/// GaussianHmmFilter and GradientBoostedTreeModel: filtering an HMM and scoring a tree ensemble
/// are both exactly reproducible, because neither involves the search that fitting them does.
/// What separates HAR from those is not feasibility but surface area -- four numbers against a
/// transition matrix or a forest -- which is why all of them are verified against parity vectors
/// rather than trusted for being simple.
///
/// What it replaces
/// ----------------
/// The volatility expert has been running on the conventional HAR weights -- 0.35 / 0.35 / 0.30 --
/// which are not fitted to anything and were documented as such. With an artifact those become the
/// fallback rather than the answer, and the forecast can finally say which of the two it used.
/// Without one, nothing changes and nothing pretends otherwise.
/// </summary>
public sealed class HarVarianceModel
{
    /// <summary>Model types this runtime has an inference path for.</summary>
    public static readonly IReadOnlySet<string> SupportedModelTypes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "har", "harq" };

    /// <summary>
    /// The feature set and ordering this runtime feeds a HAR model.
    ///
    /// Ordered, because order is what the schema hash protects. A model fitted on
    /// [intercept, daily, weekly, monthly] and fed [intercept, monthly, weekly, daily] produces
    /// confident nonsense, which is why section 4.1 makes a mismatch fatal rather than a warning.
    /// </summary>
    public static readonly IReadOnlyList<string> FeatureNames =
        ["rv_short", "rv_medium", "rv_long"];

    private const string Intercept = "intercept";
    private const string ShortCoefficient = "beta_short";
    private const string MediumCoefficient = "beta_medium";
    private const string LongCoefficient = "beta_long";

    private readonly FittedModelContract? _artifact;

    private HarVarianceModel(FittedModelContract? artifact) => _artifact = artifact;

    /// <summary>A model with no artifact, which forecasts nothing and says so.</summary>
    public static HarVarianceModel Unfitted() => new(null);

    /// <summary>
    /// Loads an artifact, or refuses it with a reason.
    /// </summary>
    public static bool TryLoad(
        FittedModelContract artifact,
        RuntimeFeatureContract runtime,
        out HarVarianceModel model,
        out FittedModelRejection rejection)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        rejection = artifact.Validate(runtime, SupportedModelTypes);
        if (rejection is not FittedModelRejection.None)
        {
            model = Unfitted();
            return false;
        }

        // Every coefficient by name, never by position. A parameters map that happens to enumerate
        // in the right order today is not a contract, and reading it positionally would reintroduce
        // exactly the ordering failure the schema hash exists to prevent.
        foreach (string required in new[] { Intercept, ShortCoefficient, MediumCoefficient, LongCoefficient })
        {
            if (!artifact.Parameters.ContainsKey(required))
            {
                rejection = FittedModelRejection.UnusableParameters;
                model = Unfitted();
                return false;
            }
        }

        var candidate = new HarVarianceModel(artifact);

        // Held to the same standard as every other model crossing this boundary. A dot product is
        // the least likely of them to be ported wrong, which is not a reason to exempt it: the
        // check costs nothing and the class of error it catches -- a coefficient read under the
        // wrong name, a feature order that drifted -- does not care how simple the arithmetic is.
        if (!artifact.ReproducesParity(candidate.Score))
        {
            rejection = FittedModelRejection.ParityCheckFailed;
            model = Unfitted();
            return false;
        }

        model = candidate;
        return true;
    }

    private IReadOnlyList<double>? Score(IReadOnlyList<IReadOnlyList<double>> inputs)
    {
        // Stateless, so a parity case is one observation. More than one would mean the fitting side
        // believes this model carries state, which it does not.
        if (inputs.Count != 1 || inputs[0].Count != FeatureNames.Count) return null;
        IReadOnlyList<double> features = inputs[0];
        return Predict(features[0], features[1], features[2]) is { } value ? [value] : null;
    }

    /// <summary>True when a validated artifact is driving the forecast.</summary>
    public bool IsFitted => _artifact is not null;

    /// <summary>The artifact behind the forecast, so a decision can be traced to its fit.</summary>
    public FittedModelContract? Artifact => _artifact;

    /// <summary>
    /// The fitted variance forecast, or null when no artifact is loaded.
    ///
    /// Null rather than a fallback, so the caller decides what to do about an absent model instead
    /// of receiving an unfitted number that looks fitted.
    /// </summary>
    public double? Predict(double shortRunVariance, double mediumVariance, double longVariance)
    {
        if (_artifact is null) return null;
        if (!double.IsFinite(shortRunVariance) || !double.IsFinite(mediumVariance)
            || !double.IsFinite(longVariance))
        {
            return null;
        }

        double predicted =
            _artifact.Parameters[Intercept]
            + (_artifact.Parameters[ShortCoefficient] * shortRunVariance)
            + (_artifact.Parameters[MediumCoefficient] * mediumVariance)
            + (_artifact.Parameters[LongCoefficient] * longVariance);

        if (!double.IsFinite(predicted)) return null;

        // A variance cannot be negative. A fitted model can produce one anyway -- least squares does
        // not know that -- and clamping is right where refusing would be precious: the model is
        // saying "as close to zero as I can express", which is information, and the intercept going
        // slightly negative on a quiet window is ordinary rather than a fault.
        return Math.Max(predicted, 0d);
    }
}
