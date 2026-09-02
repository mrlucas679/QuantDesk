using QuantDesk.Domain.Contracts;

namespace QuantDesk.Runtime.Experts;

/// <summary>
/// GARCH(1,1) conditional variance, from parameters fitted by <c>arch</c> in Python.
///
/// What the model actually is
/// --------------------------
/// One recursion: the variance expected next period is a constant, plus a share of the last
/// squared shock, plus a share of the last variance. Three fitted numbers, and the whole reason
/// GARCH persists in practice is that this tiny form captures volatility clustering about as well
/// as far larger models.
///
/// Porting it is copying three doubles, so the risk is not arithmetic. It is that the recursion is
/// stateful: the answer depends on the variance carried forward from the previous step, so a
/// runtime that seeds that state differently from the fit produces a different path from the same
/// parameters. The seed here is the unconditional variance, which is what <c>arch</c> uses and the
/// only choice that leaves the recursion at rest when nothing is happening.
///
/// Why stationarity is checked rather than assumed
/// -----------------------------------------------
/// If alpha plus beta reaches one the process has no finite unconditional variance and forecasts
/// diverge instead of reverting. A fit can land there -- on a trending or crisis window it often
/// nearly does -- and the parameters look perfectly ordinary. Refusing is the only honest handling:
/// the model is telling us it could not find a mean to revert to, and using it anyway produces
/// variance forecasts that grow without bound.
/// </summary>
public sealed class GarchVarianceModel
{
    /// <summary>Model types this inference path reproduces.</summary>
    public static readonly IReadOnlySet<string> SupportedModelTypes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "garch", "garch11" };

    /// <summary>The feature ordering this runtime feeds, and what the schema hash protects.</summary>
    public static readonly IReadOnlyList<string> FeatureNames = ["last_squared_return", "last_variance"];

    private const string Omega = "omega";
    private const string Alpha = "alpha";
    private const string Beta = "beta";

    private readonly FittedModelContract? _artifact;

    private GarchVarianceModel(FittedModelContract? artifact) => _artifact = artifact;

    public static GarchVarianceModel Unfitted() => new(null);

    public static bool TryLoad(
        FittedModelContract artifact,
        string runtimeFeatureSchemaHash,
        out GarchVarianceModel model,
        out FittedModelRejection rejection)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        model = Unfitted();

        rejection = artifact.Validate(runtimeFeatureSchemaHash, SupportedModelTypes);
        if (rejection is not FittedModelRejection.None) return false;

        foreach (string required in new[] { Omega, Alpha, Beta })
        {
            if (!artifact.Parameters.ContainsKey(required))
            {
                rejection = FittedModelRejection.UnusableParameters;
                return false;
            }
        }

        double omega = artifact.Parameters[Omega];
        double alpha = artifact.Parameters[Alpha];
        double beta = artifact.Parameters[Beta];

        // A negative constant or weight is not a GARCH fit, it is a fit that failed into a shape
        // the arithmetic still accepts.
        if (omega <= 0d || alpha < 0d || beta < 0d)
        {
            rejection = FittedModelRejection.UnusableParameters;
            return false;
        }

        // Non-stationary: no finite unconditional variance, so forecasts diverge rather than revert.
        if (alpha + beta >= 1d)
        {
            rejection = FittedModelRejection.UnsupportedModelVariant;
            return false;
        }

        var candidate = new GarchVarianceModel(artifact);

        // The runtime scores what Python scored, and the artifact is refused unless the answers
        // match. A stateful recursion is exactly where a port diverges quietly.
        if (!artifact.ReproducesParity(features => candidate.Score(features)))
        {
            rejection = FittedModelRejection.ParityCheckFailed;
            return false;
        }

        model = candidate;
        return true;
    }

    public bool IsFitted => _artifact is not null;

    public FittedModelContract? Artifact => _artifact;

    /// <summary>
    /// The variance expected next period, given the last shock and the last variance.
    /// </summary>
    public double? Predict(double lastSquaredReturn, double lastVariance)
    {
        if (_artifact is null) return null;
        if (!double.IsFinite(lastSquaredReturn) || lastSquaredReturn < 0d) return null;
        if (!double.IsFinite(lastVariance) || lastVariance < 0d) return null;

        double predicted =
            _artifact.Parameters[Omega]
            + (_artifact.Parameters[Alpha] * lastSquaredReturn)
            + (_artifact.Parameters[Beta] * lastVariance);

        return double.IsFinite(predicted) ? Math.Max(predicted, 0d) : null;
    }

    /// <summary>
    /// The variance the recursion settles to when nothing is happening.
    ///
    /// omega / (1 - alpha - beta). This is the seed the recursion starts from, and using anything
    /// else -- a zero, a sample variance from a different window -- makes the same parameters
    /// produce a different path than the fit did.
    /// </summary>
    public double? UnconditionalVariance()
    {
        if (_artifact is null) return null;

        double persistence = _artifact.Parameters[Alpha] + _artifact.Parameters[Beta];
        if (persistence >= 1d) return null;

        return _artifact.Parameters[Omega] / (1d - persistence);
    }

    private double? Score(IReadOnlyList<double> features) =>
        features.Count == FeatureNames.Count ? Predict(features[0], features[1]) : null;
}
