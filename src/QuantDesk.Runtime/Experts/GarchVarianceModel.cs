using QuantDesk.Domain.Contracts;

namespace QuantDesk.Runtime.Experts;

/// <summary>
/// GARCH(1,1) conditional variance, from parameters fitted by <c>arch</c> in Python.
///
/// What the model actually is
/// --------------------------
/// One recursion: the variance expected next period is a constant, plus a share of the last squared
/// shock, plus a share of the last variance. Three fitted numbers, and the whole reason GARCH
/// persists in practice is that this tiny form captures volatility clustering about as well as far
/// larger models.
///
/// Where the recursion starts, and a claim that was wrong
/// ------------------------------------------------------
/// Porting three doubles is not the risk. The recursion is stateful, so the answer depends on the
/// variance carried in from the previous step, and a runtime that starts it differently from the
/// fit produces a different path from identical parameters.
///
/// This class used to assert that the unconditional variance -- omega / (1 - alpha - beta) -- is
/// what <c>arch</c> starts from. It is not. <c>arch</c> backcasts: an exponentially weighted
/// average of the first 75 squared residuals with a decay of 0.94. Anything seeded from the
/// unconditional variance begins on a different path than the fit it claims to reproduce.
///
/// The repair is not to copy that initialisation, and not to ship the fit's terminal variance
/// across either -- which would make the artifact stateful, so a restart would have to restore
/// state that may by then be hours old, and somebody would have to own a staleness policy.
///
/// It is unnecessary. Because beta is below one, the seed's influence decays geometrically. On a
/// real fit (beta = 0.9175) every seed tried -- the backcast, the unconditional variance, zero, and
/// a deliberately absurd 1e6 -- converged to arch's own conditional variance path to 1.1e-16
/// relative after 1000 bars. So this runs the recursion cold over a warm-up window long enough that
/// the seed cannot survive it, and refuses to forecast before it has one. The window length comes
/// from the artifact, derived there from beta rather than chosen.
///
/// Units are not optional
/// ----------------------
/// A model fitted on percent returns cannot consume decimal returns because omega, alpha and beta
/// survived serialisation: omega is wrong by a factor of ten thousand and every forecast built on
/// it is confidently, quietly wrong. The artifact records the scale it was fitted on, and the
/// caller is responsible for feeding the same one.
///
/// Why stationarity is checked rather than assumed
/// -----------------------------------------------
/// If alpha plus beta reaches one the process has no finite unconditional variance and forecasts
/// diverge instead of reverting. A fit can land there -- on a trending or crisis window it often
/// nearly does -- and the parameters look perfectly ordinary. Refusing is the only honest handling:
/// the model is telling us it could not find a mean to revert to.
/// </summary>
public sealed class GarchVarianceModel
{
    /// <summary>Model types this inference path reproduces.</summary>
    public static readonly IReadOnlySet<string> SupportedModelTypes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "garch", "garch11" };

    /// <summary>The feature this runtime feeds, one per bar of the warm-up window.</summary>
    public static readonly IReadOnlyList<string> FeatureNames = ["squared_residual"];

    /// <summary>The only horizon this version answers for.</summary>
    public const string SupportedHorizon = "one_step";

    /// <summary>
    /// The warm-up the *schema* declares, which is the class maximum rather than any one fit's.
    ///
    /// How much history a particular fit needs to settle depends on beta, so putting it in the
    /// schema would make the schema hash depend on the fitted parameters -- and the runtime could
    /// then never state in advance which schema it expects, collapsing the check back to comparing
    /// the artifact against itself. The fit's own warm-up travels in the variant, where the loader
    /// reads it and refuses to forecast before it has that much history.
    /// </summary>
    public const int SchemaLookbackBars = 5000;

    private const string Omega = "omega";
    private const string Alpha = "alpha";
    private const string Beta = "beta";
    private const string HorizonKey = "horizon";
    private const string WarmupKey = "warmup_bars";
    private const string ReturnUnitsKey = "return_units";

    private readonly FittedModelContract? _artifact;
    private readonly int _warmupBars;

    private GarchVarianceModel(FittedModelContract? artifact, int warmupBars)
    {
        _artifact = artifact;
        _warmupBars = warmupBars;
    }

    public static GarchVarianceModel Unfitted() => new(null, 0);

    public static bool TryLoad(
        FittedModelContract artifact,
        RuntimeFeatureContract runtime,
        out GarchVarianceModel model,
        out FittedModelRejection rejection)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        model = Unfitted();

        rejection = artifact.Validate(runtime, SupportedModelTypes);
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

        // Multi-horizon forecasting is a different recursion -- arch uses the realised residual for
        // the first step and an expectation involving alpha plus beta thereafter. Rather than
        // approximate it, this version answers one step and refuses to pretend about the rest.
        if (!artifact.Variant.TryGetValue(HorizonKey, out string? horizon)
            || !string.Equals(horizon, SupportedHorizon, StringComparison.OrdinalIgnoreCase))
        {
            rejection = FittedModelRejection.UnsupportedModelVariant;
            return false;
        }

        // The scale the fit was on. Absent, there is no way to tell whether the caller's returns
        // match it, and omega silently carries the wrong order of magnitude.
        if (!artifact.Variant.ContainsKey(ReturnUnitsKey))
        {
            rejection = FittedModelRejection.UnsupportedModelVariant;
            return false;
        }

        if (!TryWarmupBars(artifact, out int warmupBars))
        {
            rejection = FittedModelRejection.UnusableParameters;
            return false;
        }

        var candidate = new GarchVarianceModel(artifact, warmupBars);

        // The runtime warms the recursion the way it will in production and must land where arch
        // landed. A stateful recursion is exactly where a port diverges quietly.
        if (!artifact.ReproducesParity(candidate.ScoreForParity))
        {
            rejection = FittedModelRejection.ParityCheckFailed;
            return false;
        }

        model = candidate;
        return true;
    }

    public bool IsFitted => _artifact is not null;

    public FittedModelContract? Artifact => _artifact;

    /// <summary>How many bars of history the recursion needs before its answer means anything.</summary>
    public int WarmupBars => _warmupBars;

    /// <summary>The scale of the returns this model was fitted on, which the caller must match.</summary>
    public string? ReturnUnits =>
        _artifact is not null && _artifact.Variant.TryGetValue(ReturnUnitsKey, out string? units)
            ? units
            : null;

    /// <summary>
    /// The conditional variance at the end of a warm-up window, run cold from zero.
    ///
    /// Cold on purpose. The seed is deliberately not the one <c>arch</c> used, because the point is
    /// that after this many bars it makes no difference -- and if it ever did, the parity check
    /// would fail rather than the runtime quietly tracking a different path.
    /// </summary>
    /// <param name="squaredResiduals">One per bar, oldest first, on the fitted scale.</param>
    public double? WarmedVariance(IReadOnlyList<double> squaredResiduals)
    {
        if (_artifact is null) return null;
        ArgumentNullException.ThrowIfNull(squaredResiduals);

        // Short of the window the seed has not decayed out yet, so the answer would depend on a
        // number nobody chose. Refusing is the honest response; a warm-up is not a suggestion.
        if (squaredResiduals.Count < _warmupBars) return null;

        double omega = _artifact.Parameters[Omega];
        double alpha = _artifact.Parameters[Alpha];
        double beta = _artifact.Parameters[Beta];

        double variance = 0d;
        foreach (double squaredResidual in squaredResiduals)
        {
            if (!double.IsFinite(squaredResidual) || squaredResidual < 0d) return null;
            variance = omega + (alpha * squaredResidual) + (beta * variance);
        }

        return double.IsFinite(variance) ? Math.Max(variance, 0d) : null;
    }

    /// <summary>
    /// One step of the recursion, given the last shock and the last variance.
    ///
    /// For a caller that already holds a warmed variance. Use <see cref="WarmedVariance"/> to
    /// obtain one: passing an arbitrary starting variance here reproduces nothing in particular.
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
    /// The variance the recursion settles to when nothing is happening: omega / (1 - alpha - beta).
    ///
    /// Reported because it says what the model thinks normal looks like, which is useful context
    /// for a forecast. It is *not* the seed -- <c>arch</c> backcasts instead, and this runtime warms
    /// up rather than seeding at all.
    /// </summary>
    public double? UnconditionalVariance()
    {
        if (_artifact is null) return null;

        double persistence = _artifact.Parameters[Alpha] + _artifact.Parameters[Beta];
        if (persistence >= 1d) return null;

        return _artifact.Parameters[Omega] / (1d - persistence);
    }

    private IReadOnlyList<double>? ScoreForParity(IReadOnlyList<IReadOnlyList<double>> sequence)
    {
        var squaredResiduals = new List<double>(sequence.Count);
        foreach (IReadOnlyList<double> observation in sequence)
        {
            if (observation.Count != FeatureNames.Count) return null;
            squaredResiduals.Add(observation[0]);
        }

        return WarmedVariance(squaredResiduals) is { } variance ? [variance] : null;
    }

    private static bool TryWarmupBars(FittedModelContract artifact, out int bars)
    {
        bars = 0;
        if (!artifact.Variant.TryGetValue(WarmupKey, out string? raw)) return false;
        if (!int.TryParse(raw, out int parsed) || parsed < 1) return false;

        bars = parsed;
        return true;
    }
}
