using QuantDesk.Domain.Contracts;

namespace QuantDesk.Runtime.Experts;

/// <summary>
/// Forward filtering for a Gaussian hidden Markov model fitted by <c>hmmlearn</c>.
///
/// Why this is portable after all
/// ------------------------------
/// An earlier note in this codebase claimed an HMM could not cross the language boundary. That was
/// wrong, and worth correcting rather than quietly working around. Filtering -- the only thing a
/// live system needs -- is one recursion: propagate the previous state belief through the
/// transition matrix, weight it by how likely each state makes the observation, renormalise. For a
/// Gaussian model with diagonal covariance the emission term is a product of one-dimensional
/// normal densities. It is exact, it is perhaps sixty lines, and it involves no optimisation.
///
/// What was true is that an HMM has far more surface to get wrong than a HAR fit: a transition
/// matrix that is transposed, a covariance read as a standard deviation, a state ordering that
/// differs between fit and load. Every one of those produces confident, plausible, wrong
/// probabilities. That is why nothing here is trusted without the artifact's parity vectors.
///
/// What it refuses
/// ---------------
/// Full or tied covariance, because reproducing those means a Cholesky factorisation and a matrix
/// inverse whose conditioning behaviour would have to match another library's exactly. Non-Gaussian
/// emissions, for the same reason. Refusing is not a limitation to apologise for -- an approximation
/// of a regime model is worse than no regime model, because the exit engine acts on it.
///
/// Training stays in Python
/// ------------------------
/// Baum-Welch is iterative, sensitive to initialisation, and prone to degenerate states. Section
/// 20.3 requires those to be rejected during fitting, which is a research judgement, not a runtime
/// one. This filters; it never fits.
/// </summary>
public sealed class GaussianHmmFilter
{
    /// <summary>Model types this inference path reproduces.</summary>
    public static readonly IReadOnlySet<string> SupportedModelTypes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "hmm", "gaussian_hmm" };

    /// <summary>Covariance shapes this filter reproduces exactly.</summary>
    public static readonly IReadOnlySet<string> SupportedCovarianceTypes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "diag", "spherical" };

    private const string CovarianceTypeKey = "covariance_type";
    private const string StatesKey = "n_states";
    private const string FeaturesKey = "n_features";

    private readonly FittedModelContract? _artifact;
    private readonly int _states;
    private readonly int _features;
    private readonly double[] _startProbabilities;
    private readonly double[,] _transitions;
    private readonly double[,] _means;
    private readonly double[,] _variances;

    private GaussianHmmFilter(
        FittedModelContract? artifact,
        int states,
        int features,
        double[] startProbabilities,
        double[,] transitions,
        double[,] means,
        double[,] variances)
    {
        _artifact = artifact;
        _states = states;
        _features = features;
        _startProbabilities = startProbabilities;
        _transitions = transitions;
        _means = means;
        _variances = variances;
    }

    public static GaussianHmmFilter Unfitted() =>
        new(null, 0, 0, [], new double[0, 0], new double[0, 0], new double[0, 0]);

    public static bool TryLoad(
        FittedModelContract artifact,
        string runtimeFeatureSchemaHash,
        out GaussianHmmFilter model,
        out FittedModelRejection rejection)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        model = Unfitted();

        rejection = artifact.Validate(runtimeFeatureSchemaHash, SupportedModelTypes);
        if (rejection is not FittedModelRejection.None) return false;

        // Full and tied covariance need a factorisation whose conditioning behaviour would have to
        // match another library's exactly. An approximation of a regime model is worse than none,
        // because the exit engine acts on it.
        string covariance = artifact.Variant.TryGetValue(CovarianceTypeKey, out string? kind)
            ? kind
            : string.Empty;
        if (!SupportedCovarianceTypes.Contains(covariance))
        {
            rejection = FittedModelRejection.UnsupportedModelVariant;
            return false;
        }

        if (!TryDimension(artifact, StatesKey, out int states)
            || !TryDimension(artifact, FeaturesKey, out int features))
        {
            rejection = FittedModelRejection.UnusableParameters;
            return false;
        }

        double[] start = new double[states];
        double[,] transitions = new double[states, states];
        double[,] means = new double[states, features];
        double[,] variances = new double[states, features];

        // Every value addressed by name. A flat parameter map read positionally would depend on
        // dictionary ordering, which is exactly the class of failure the schema hash exists to stop.
        for (int i = 0; i < states; i++)
        {
            if (!artifact.Parameters.TryGetValue($"start_{i}", out double startValue))
            {
                rejection = FittedModelRejection.UnusableParameters;
                return false;
            }

            start[i] = startValue;

            for (int j = 0; j < states; j++)
            {
                if (!artifact.Parameters.TryGetValue($"trans_{i}_{j}", out double transition))
                {
                    rejection = FittedModelRejection.UnusableParameters;
                    return false;
                }

                transitions[i, j] = transition;
            }

            for (int f = 0; f < features; f++)
            {
                if (!artifact.Parameters.TryGetValue($"mean_{i}_{f}", out double mean)
                    || !artifact.Parameters.TryGetValue($"var_{i}_{f}", out double variance))
                {
                    rejection = FittedModelRejection.UnusableParameters;
                    return false;
                }

                // A zero or negative variance makes the density undefined. hmmlearn floors these
                // during fitting; an artifact carrying one has been damaged in transit.
                if (variance <= 0d)
                {
                    rejection = FittedModelRejection.UnusableParameters;
                    return false;
                }

                means[i, f] = mean;
                variances[i, f] = variance;
            }
        }

        if (!IsStochastic(start) || !RowsAreStochastic(transitions, states))
        {
            rejection = FittedModelRejection.UnusableParameters;
            return false;
        }

        var candidate = new GaussianHmmFilter(
            artifact, states, features, start, transitions, means, variances);

        // The guard that makes a reimplemented filter trustworthy. A transposed transition matrix
        // and a correct one both produce a valid probability vector; only the parity vectors tell
        // them apart.
        if (!artifact.ReproducesParity(candidate.ScoreForParity))
        {
            rejection = FittedModelRejection.ParityCheckFailed;
            return false;
        }

        model = candidate;
        return true;
    }

    public bool IsFitted => _artifact is not null;

    public int StateCount => _states;

    /// <summary>
    /// One filtering step: the posterior over states given a new observation and the previous
    /// belief.
    /// </summary>
    /// <param name="observation">The feature vector, in the schema's order.</param>
    /// <param name="previous">The previous posterior, or null to start from the fitted prior.</param>
    public double[]? Filter(IReadOnlyList<double> observation, double[]? previous = null)
    {
        if (_artifact is null) return null;
        ArgumentNullException.ThrowIfNull(observation);
        if (observation.Count != _features) return null;

        foreach (double value in observation)
        {
            if (!double.IsFinite(value)) return null;
        }

        // Prior on the first step, propagated belief afterwards.
        double[] predicted = new double[_states];
        if (previous is null || previous.Length != _states)
        {
            Array.Copy(_startProbabilities, predicted, _states);
        }
        else
        {
            for (int j = 0; j < _states; j++)
            {
                double sum = 0d;
                for (int i = 0; i < _states; i++) sum += previous[i] * _transitions[i, j];
                predicted[j] = sum;
            }
        }

        // Emission likelihood in logs, then shifted before exponentiating. Multiplying densities
        // directly underflows to zero for even a handful of features, which turns the posterior
        // into a division of zero by zero -- silently, and only on the quiet days when densities
        // are small.
        double[] logLikelihood = new double[_states];
        for (int i = 0; i < _states; i++)
        {
            double total = 0d;
            for (int f = 0; f < _features; f++)
            {
                double difference = observation[f] - _means[i, f];
                double variance = _variances[i, f];
                total += -0.5d * ((Math.Log(2d * Math.PI * variance)) + (difference * difference / variance));
            }

            // A zero prior is zero, not the smallest representable double. Flooring it at
            // double.Epsilon gives an impossible state a log-weight of about -744 instead of
            // negative infinity, and on a bar where every reachable state sits below that -- which
            // many features and small densities make ordinary -- the unreachable one wins. The
            // exponentiation below turns negative infinity into a clean zero, which is the answer.
            logLikelihood[i] = predicted[i] > 0d
                ? total + Math.Log(predicted[i])
                : double.NegativeInfinity;
        }

        double maximum = double.NegativeInfinity;
        foreach (double value in logLikelihood) maximum = Math.Max(maximum, value);
        if (!double.IsFinite(maximum)) return null;

        double[] posterior = new double[_states];
        double normaliser = 0d;
        for (int i = 0; i < _states; i++)
        {
            posterior[i] = Math.Exp(logLikelihood[i] - maximum);
            normaliser += posterior[i];
        }

        if (normaliser <= 0d || !double.IsFinite(normaliser)) return null;
        for (int i = 0; i < _states; i++) posterior[i] /= normaliser;

        return posterior;
    }

    /// <summary>
    /// The parity surface: filter the whole sequence, and report the entire posterior.
    ///
    /// Both halves of that are corrections. This used to filter a single observation and return one
    /// state's probability, which meant the belief always started from the fitted prior -- so the
    /// transition matrix was never applied, and a transposed one passed the check as readily as the
    /// right one. Returning one number rather than the vector hid the rest of the distribution,
    /// where a mislabelled state shows up.
    ///
    /// The expected vectors come from hmmlearn's own filtered posterior, which is not the same as
    /// its smoothed one: predict_proba over a whole sequence is informed by observations that came
    /// after each step, and the runtime has none. They agree only at the final row, so a fixture
    /// built the obvious way passes a spot-check and is wrong everywhere else.
    /// </summary>
    private IReadOnlyList<double>? ScoreForParity(IReadOnlyList<IReadOnlyList<double>> sequence)
    {
        double[]? posterior = null;
        foreach (IReadOnlyList<double> observation in sequence)
        {
            posterior = Filter(observation, posterior);
            if (posterior is null) return null;
        }

        return posterior;
    }

    private static bool TryDimension(FittedModelContract artifact, string key, out int value)
    {
        value = 0;
        if (!artifact.Parameters.TryGetValue(key, out double raw)) return false;
        if (!double.IsFinite(raw) || raw < 1d || raw > 64d) return false;
        if (Math.Abs(raw - Math.Round(raw)) > 1e-9d) return false;

        value = (int)Math.Round(raw);
        return true;
    }

    private static bool IsStochastic(double[] values)
    {
        double sum = 0d;
        foreach (double value in values)
        {
            if (!double.IsFinite(value) || value < 0d) return false;
            sum += value;
        }

        // Tolerant, because the fit wrote decimal text and the sum of its parts need not be exactly
        // one; strict enough that a genuinely malformed vector is caught.
        return Math.Abs(sum - 1d) < 1e-6d;
    }

    private static bool RowsAreStochastic(double[,] matrix, int states)
    {
        for (int i = 0; i < states; i++)
        {
            double[] row = new double[states];
            for (int j = 0; j < states; j++) row[j] = matrix[i, j];
            if (!IsStochastic(row)) return false;
        }

        return true;
    }
}
