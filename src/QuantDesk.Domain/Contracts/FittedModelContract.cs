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
}

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

        return FittedModelRejection.None;
    }

    public bool IsUsableBy(string runtimeFeatureSchemaHash, IReadOnlySet<string> supportedModelTypes) =>
        Validate(runtimeFeatureSchemaHash, supportedModelTypes) is FittedModelRejection.None;
}
