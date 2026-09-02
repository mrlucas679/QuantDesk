namespace QuantDesk.Domain.Contracts;

/// <summary>Why a model artifact may not be used.</summary>
public enum FittedModelRejection
{
    /// <summary>It may. Not a rejection.</summary>
    None,

    /// <summary>A required field of the manifest is missing or empty.</summary>
    IncompleteManifest,

    /// <summary>The runtime computes a different feature set than the model was fitted on.</summary>
    FeatureSchemaMismatch,

    /// <summary>The artifact is of a type this runtime has no inference path for.</summary>
    UnsupportedModelType,

    /// <summary>Parameters are absent, the wrong shape, or not finite.</summary>
    UnusableParameters,

    /// <summary>The artifact has not been promoted far enough to inform a decision.</summary>
    InsufficientPromotion,

    /// <summary>The runtime could not reproduce the outputs the fitting process recorded.</summary>
    ParityCheckFailed,

    /// <summary>The artifact uses a model variant this runtime cannot reproduce exactly.</summary>
    UnsupportedModelVariant,
}

/// <summary>
/// One input the fitting process scored, and the answer it got.
/// </summary>
/// <param name="Features">The input vector, in the schema's order.</param>
/// <param name="ExpectedOutput">What the fitted model produced for it, in Python.</param>
public sealed record ModelParityCheck(IReadOnlyList<double> Features, double ExpectedOutput);

/// <summary>
/// A model fitted in Python, described completely enough that the runtime can refuse it.
///
/// Distinct from <see cref="ModelArtifactContract"/>, which describes a *strategy* artifact and
/// carries the R-gate evidence that licenses one to trade. This describes a *fitted model* and
/// carries the coefficients an inference path needs. A system can have either without the other:
/// a strategy licensed to trade with no model behind it, or a fitted model with no strategy yet
/// entitled to use it, and collapsing the two would make each claim the other's guarantees.
///
/// The point of the manifest is refusal
/// ------------------------------------
/// Section 4.1 makes a feature-schema mismatch a hard model-invalid condition and prohibits
/// best-effort field reordering, which is a strong statement about a weak failure. A model fitted
/// on features in one order and fed them in another does not throw and does not look wrong: it
/// produces confident numbers from coefficients matched to the wrong inputs, and every downstream
/// gate treats them as a forecast. There is no recovering from that after the fact, so the only
/// safe behaviour is to compare hashes and decline.
///
/// Everything else here exists so a live decision can be traced back to the fit that justified it:
/// which dataset, which window, which seed, which commit. Section 20.5 lists them, and the reason
/// they matter is the one this system learned the hard way -- the 26 registered strategy figures
/// have none of this, and when the cost assumption behind them turned out to be wrong there was no
/// way to tell which conclusions depended on it.
/// </summary>
/// <param name="ArtifactId">Unique identity of this fitted artifact.</param>
/// <param name="ModelId">Which model this is a version of.</param>
/// <param name="ModelType">The family, which decides whether an inference path exists.</param>
/// <param name="ModelVersion">Version of the fitted model.</param>
/// <param name="FeatureSchemaHash">Hash of the exact feature set and ordering it was fitted on.</param>
/// <param name="DatasetHash">Hash of the data it was fitted from.</param>
/// <param name="Parameters">The fitted values, keyed by name.</param>
/// <param name="RandomSeed">Seed, so the fit can be reproduced.</param>
/// <param name="EvidenceGrade">How far the evidence behind it was taken.</param>
/// <param name="PromotionState">Where it sits on the ladder in section 20.4.</param>
/// <param name="GitCommit">The commit that produced it.</param>
/// <param name="CreatedAt">When it was fitted.</param>
public sealed record FittedModelContract(
    string ArtifactId,
    string ModelId,
    string ModelType,
    string ModelVersion,
    string FeatureSchemaHash,
    string DatasetHash,
    IReadOnlyDictionary<string, double> Parameters,
    int RandomSeed,
    string EvidenceGrade,
    string PromotionState,
    string GitCommit,
    DateTimeOffset CreatedAt)
{
    /// <summary>
    /// Inputs the fitting process scored, with the answers it got.
    ///
    /// The mechanism that makes a reimplemented inference path safe rather than hopeful. Every
    /// model crossing this boundary is evaluated twice -- once by the library that fitted it and
    /// once by code written here -- and the failure mode is not a crash but a quiet divergence:
    /// a tree traversal that resolves a tie the other way, a missing-value branch taken
    /// differently, a covariance read as variance. None of those throw. All of them produce
    /// confident numbers that are wrong in a way nothing downstream can detect.
    ///
    /// So the artifact carries the answers. On load, the runtime scores the same inputs with its
    /// own code and refuses the artifact unless it reproduces them. That converts an unbounded
    /// class of silent porting errors into one loud one at startup, and it is the only reason to
    /// trust an inference path nobody has diffed line by line against the original.
    /// </summary>
    public IReadOnlyList<ModelParityCheck> ParityChecks { get; init; } = [];

    /// <summary>
    /// Free-form variant description from the fitting process -- covariance type, objective,
    /// whether categorical splits were used. Each loader refuses what it cannot reproduce.
    /// </summary>
    public IReadOnlyDictionary<string, string> Variant { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Tolerance a parity check must fall within.
    ///
    /// Loose enough to allow the last bits of double arithmetic to differ between two languages
    /// summing the same terms in a different order, tight enough that any real disagreement --
    /// a different branch, a transposed matrix, a wrong sign -- is far outside it.
    /// </summary>
    public const double ParityTolerance = 1e-9d;
    /// <summary>Promotion states at which an artifact may inform a live decision.</summary>
    public static readonly IReadOnlySet<string> DecisionCapableStates =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "VALIDATED", "SHADOW", "EXPLORATION", "EXPLOITATION",
        };

    /// <summary>
    /// Whether this artifact may be used, given what the runtime actually computes.
    /// </summary>
    /// <param name="runtimeFeatureSchemaHash">
    /// Hash of the feature set the runtime will feed it. A mismatch is fatal by design.
    /// </param>
    /// <param name="supportedModelTypes">Types this runtime has an inference path for.</param>
    public FittedModelRejection Validate(
        string runtimeFeatureSchemaHash,
        IReadOnlySet<string> supportedModelTypes)
    {
        ArgumentNullException.ThrowIfNull(supportedModelTypes);

        if (string.IsNullOrWhiteSpace(ArtifactId)
            || string.IsNullOrWhiteSpace(ModelId)
            || string.IsNullOrWhiteSpace(ModelType)
            || string.IsNullOrWhiteSpace(ModelVersion)
            || string.IsNullOrWhiteSpace(FeatureSchemaHash)
            || string.IsNullOrWhiteSpace(DatasetHash)
            || string.IsNullOrWhiteSpace(GitCommit))
        {
            return FittedModelRejection.IncompleteManifest;
        }

        if (!supportedModelTypes.Contains(ModelType))
            return FittedModelRejection.UnsupportedModelType;

        // The hard one. Not a warning, not a reorder, not a best effort -- a model fed features in
        // an order it was not fitted on produces confident numbers from the wrong coefficients, and
        // nothing downstream can tell.
        if (!string.Equals(FeatureSchemaHash, runtimeFeatureSchemaHash, StringComparison.Ordinal))
            return FittedModelRejection.FeatureSchemaMismatch;

        if (Parameters is null || Parameters.Count == 0) return FittedModelRejection.UnusableParameters;
        foreach (double value in Parameters.Values)
        {
            if (!double.IsFinite(value)) return FittedModelRejection.UnusableParameters;
        }

        // An experimental or retired artifact is a record of work, not a licence to decide with it.
        if (!DecisionCapableStates.Contains(PromotionState))
            return FittedModelRejection.InsufficientPromotion;

        // A model with no parity checks cannot be verified, and an unverified reimplementation is
        // the thing this whole contract exists to prevent.
        if (ParityChecks.Count == 0) return FittedModelRejection.ParityCheckFailed;

        return FittedModelRejection.None;
    }

    public bool IsUsableBy(string runtimeFeatureSchemaHash, IReadOnlySet<string> supportedModelTypes) =>
        Validate(runtimeFeatureSchemaHash, supportedModelTypes) is FittedModelRejection.None;

    /// <summary>
    /// Whether an inference path reproduces every answer the fitting process recorded.
    /// </summary>
    /// <param name="score">The runtime's own inference over a feature vector, or null if it cannot.</param>
    public bool ReproducesParity(Func<IReadOnlyList<double>, double?> score)
    {
        ArgumentNullException.ThrowIfNull(score);
        if (ParityChecks.Count == 0) return false;

        foreach (ModelParityCheck check in ParityChecks)
        {
            double? actual = score(check.Features);
            if (actual is not { } value || !double.IsFinite(value)) return false;

            // Relative where the expected value is large enough for relative to mean anything,
            // absolute near zero -- a fixed relative tolerance is meaningless at 1e-300 and a fixed
            // absolute one is meaningless at 1e6.
            double scale = Math.Max(1d, Math.Abs(check.ExpectedOutput));
            if (Math.Abs(value - check.ExpectedOutput) > ParityTolerance * scale) return false;
        }

        return true;
    }
}
