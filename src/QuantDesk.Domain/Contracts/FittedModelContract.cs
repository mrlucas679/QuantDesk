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

    /// <summary>The runtime could not reproduce the outputs the fitting library recorded.</summary>
    ParityCheckFailed,

    /// <summary>The artifact uses a model variant this runtime cannot reproduce exactly.</summary>
    UnsupportedModelVariant,

    /// <summary>The artifact's contents do not match the hash it was sealed under.</summary>
    ArtifactHashMismatch,

    /// <summary>The artifact was written against a contract version this runtime does not read.</summary>
    UnsupportedArtifactVersion,

    /// <summary>
    /// The runtime computes the declared features, but not in the units the model was fitted on.
    ///
    /// Separate from a schema mismatch because it is a separate failure. The hash proves the
    /// feature set and its ordering agree; it says nothing about what the numbers mean, so a model
    /// fitted on percent returns and fed decimals passes it and is wrong by four orders of
    /// magnitude.
    /// </summary>
    FeatureSemanticsMismatch,
}

/// <summary>
/// One input the fitting library scored, and the answer it gave.
///
/// The input is a sequence of observations even for a stateless model, where it is a sequence of
/// one. One shape for both kinds, rather than two structures that drift apart -- and it makes the
/// stateful case, where the answer depends on everything before it, the default rather than the
/// exception. That matters: the previous shape was one observation to one number, and under it the
/// HMM's parity ran a single filtering step from the fitted prior, so the transition matrix was
/// never exercised at all.
///
/// A missing feature arrives as NaN, from a JSON null. JSON has no NaN literal and .NET's parser
/// refuses the bare token, so the wire format cannot carry one.
/// </summary>
/// <param name="Inputs">The observation sequence, each in the schema's order.</param>
/// <param name="Expected">What the fitting library produced, in Python.</param>
public sealed record ModelParityCheck(
    IReadOnlyList<IReadOnlyList<double>> Inputs,
    IReadOnlyList<double> Expected);

/// <summary>What a parity case's shape means, which differs by family.</summary>
public enum ParityKind
{
    /// <summary>One observation in, one number out: HAR, and a tree ensemble.</summary>
    VectorToScalar,

    /// <summary>A sequence in, a vector out: an HMM posterior, or a warmed GARCH recursion.</summary>
    SequenceToVector,
}

/// <summary>
/// How close counts as agreement, per family rather than once for everything.
///
/// One number cannot mean the same thing for a variance of 1e-8, a return in basis points and a
/// probability bounded by one. A relative bound on a posterior of 1e-16 demands precision neither
/// language's arithmetic delivers; an absolute bound on a variance of 1e-8 accepts a model that is
/// entirely wrong. So the fitting side states both, and the runtime applies whichever the family's
/// numbers make meaningful.
/// </summary>
/// <param name="Absolute">Allowed absolute difference.</param>
/// <param name="Relative">Allowed difference as a fraction of the expected magnitude.</param>
public readonly record struct ParityTolerance(double Absolute, double Relative)
{
    public bool Accepts(double actual, double expected)
    {
        if (!double.IsFinite(actual) || !double.IsFinite(expected)) return false;
        double difference = Math.Abs(actual - expected);
        return difference <= Absolute || difference <= Relative * Math.Abs(expected);
    }
}

/// <summary>One node of a decision tree: either a split or a leaf.</summary>
/// <param name="SplitFeature">Index into the feature vector, or -1 at a leaf.</param>
/// <param name="Threshold">Values at or below this go left. Meaningless at a leaf.</param>
/// <param name="MissingType">Which values count as missing here. See <see cref="TreeMissingType"/>.</param>
/// <param name="DefaultLeft">Which way a missing value goes.</param>
/// <param name="Left">Index of the left child, or -1 at a leaf.</param>
/// <param name="Right">Index of the right child, or -1 at a leaf.</param>
/// <param name="LeafValue">The contribution, at a leaf.</param>
public readonly record struct TreeNode(
    int SplitFeature,
    double Threshold,
    TreeMissingType MissingType,
    bool DefaultLeft,
    int Left,
    int Right,
    double LeafValue)
{
    public bool IsLeaf => SplitFeature < 0;
}

/// <summary>
/// Which values a split treats as missing.
///
/// Carried per node, not per model, because LightGBM records it per node and the three conventions
/// route the same input to different leaves. Collapsing them to one rule is the defect this type
/// exists to make impossible to write.
/// </summary>
public enum TreeMissingType
{
    /// <summary>Nothing is missing. A NaN becomes zero and meets the threshold like any value.</summary>
    None,

    /// <summary>A NaN is missing and takes the default branch. Zero is an ordinary value.</summary>
    NaN,

    /// <summary>A NaN, and anything within the zero bound, is missing and takes the default branch.</summary>
    Zero,
}

/// <summary>A flattened decision tree. Node 0 is the root.</summary>
public sealed record DecisionTree(IReadOnlyList<TreeNode> Nodes);

/// <summary>
/// A model fitted in Python, described completely enough that the runtime can refuse it.
///
/// Distinct from <see cref="ModelArtifactContract"/>, which describes a *strategy* artifact and
/// carries the R-gate evidence that licenses one to trade. This describes a *fitted model* and
/// carries the numbers an inference path needs. A system can have either without the other: a
/// strategy licensed to trade with no model behind it, or a fitted model with no strategy yet
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
/// which library at which version, which dataset, which seed, which commit. Section 20.5 lists
/// them, and the reason they matter is the one this system learned the hard way -- the 26
/// registered strategy figures have none of this, and when the cost assumption behind them turned
/// out to be wrong there was no way to tell which conclusions depended on it.
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
    /// <summary>The contract version this runtime reads.</summary>
    public const string SupportedSchemaVersion = "runtime-inference-v2";

    /// <summary>The version the artifact was written against.</summary>
    public string ArtifactSchemaVersion { get; init; } = SupportedSchemaVersion;

    /// <summary>The library that fitted it, and at which version.</summary>
    public string ProducerLibrary { get; init; } = string.Empty;

    /// <summary>Version of that library, so a parity failure can be diagnosed rather than guessed.</summary>
    public string ProducerLibraryVersion { get; init; } = string.Empty;

    /// <summary>
    /// Inputs the fitting library scored, with the answers it gave.
    ///
    /// The mechanism that makes a reimplemented inference path safe rather than hopeful. Every
    /// model crossing this boundary is evaluated twice -- once by the library that fitted it and
    /// once by code written here -- and the failure mode is not a crash but a quiet divergence: a
    /// tree traversal that resolves a tie the other way, a missing-value branch taken differently,
    /// a covariance read as a variance. None of those throw. All produce confident numbers that are
    /// wrong in a way nothing downstream can detect.
    ///
    /// So the artifact carries the answers, and on load the runtime scores the same inputs with its
    /// own code and refuses unless it reproduces them. That converts an unbounded class of silent
    /// porting errors into one loud one at startup.
    ///
    /// This only means anything if the answers came from the library. They did not, at first: the
    /// vectors in this codebase's own tests were computed by the C# implementations they were
    /// checking, which is a test of the code against its own arithmetic and passes just as happily
    /// when the arithmetic is wrong.
    /// </summary>
    public IReadOnlyList<ModelParityCheck> ParityChecks { get; init; } = [];

    /// <summary>What the parity cases' shape means for this family.</summary>
    public ParityKind ParityKind { get; init; } = ParityKind.VectorToScalar;

    /// <summary>
    /// How close the runtime must come, as the fitting side stated it.
    ///
    /// Defaulted loosely enough that a hand-built contract still works, but a real artifact carries
    /// its family's own bounds rather than inheriting one number chosen for something else.
    /// </summary>
    public ParityTolerance Tolerance { get; init; } = new(1e-18d, 1e-9d);

    /// <summary>
    /// The trees, for an ensemble.
    ///
    /// Inside the artifact, because the trees *are* the model. Passing them alongside it -- as this
    /// bridge first did, through a separate argument to the loader -- produces an artifact that
    /// hashes everything except the part which decides the answer.
    /// </summary>
    public IReadOnlyList<DecisionTree> Trees { get; init; } = [];

    /// <summary>
    /// The bound below which a value counts as zero, for <see cref="TreeMissingType.Zero"/>.
    ///
    /// Carried rather than hard-coded because it is a property of the producing library, and
    /// getting it from the wrong width is a real defect: LightGBM's literal is a float, so it
    /// widens to 1.0000000180025095e-35 and not to the double 1e-35, and values between the two
    /// route differently.
    /// </summary>
    public double ZeroThreshold { get; init; } = 1.0000000180025095e-35d;

    /// <summary>
    /// Free-form variant description from the fitting process -- covariance type, objective,
    /// whether categorical splits were used. Each loader refuses what it cannot reproduce.
    /// </summary>
    public IReadOnlyDictionary<string, string> Variant { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// What this model was fitted on, and therefore what it may be asked about.
    ///
    /// Undeclared for anything written before the field existed, and undeclared supports nothing.
    /// Reading silence as universal permission is exactly the behaviour that let one BTC-fitted HAR
    /// serve four equity ETFs.
    /// </summary>
    public ExpertSupportDomain SupportDomain { get; init; } = ExpertSupportDomain.Undeclared;

    /// <summary>The hash the artifact was sealed under, covering every other field.</summary>
    public string ArtifactHash { get; init; } = string.Empty;

    /// <summary>
    /// What the fit says its features mean -- units, missing policy, warm-up, bar duration.
    ///
    /// Empty on a contract built in memory without them, which the loader treats as a refusal
    /// rather than a pass: a model whose units nobody stated is a model whose units nobody knows.
    /// </summary>
    public FeatureSemanticsContract? FeatureSemantics { get; init; }

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
        RuntimeFeatureContract runtime,
        IReadOnlySet<string> supportedModelTypes)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(supportedModelTypes);

        if (!string.Equals(ArtifactSchemaVersion, SupportedSchemaVersion, StringComparison.Ordinal))
            return FittedModelRejection.UnsupportedArtifactVersion;

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
        if (!string.Equals(FeatureSchemaHash, runtime.FeatureSchemaHash, StringComparison.Ordinal))
            return FittedModelRejection.FeatureSchemaMismatch;

        // Only meaningful once the caller has said what it computes. A caller that declares nothing
        // is checked on the hash alone, which is the older and weaker guarantee -- so the loaders
        // that matter pass a full contract, and this stays here for the refusal paths that never
        // reach a forecast.
        if (runtime.DeclaresSemantics)
        {
            if (FeatureSemantics is null || !FeatureSemantics.Accepts(runtime))
                return FittedModelRejection.FeatureSemanticsMismatch;
        }

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

        foreach (ModelParityCheck check in ParityChecks)
        {
            if (check.Inputs.Count == 0 || check.Expected.Count == 0)
                return FittedModelRejection.ParityCheckFailed;

            // A single-observation case for a stateful model starts from the fitted prior and never
            // propagates belief, so the transition matrix -- or the warmed recursion -- goes
            // entirely unchecked. That is not a weaker test; it is a test of something else.
            if (ParityKind is ParityKind.SequenceToVector && check.Inputs.Count < 2)
                return FittedModelRejection.ParityCheckFailed;
        }

        return FittedModelRejection.None;
    }

    public bool IsUsableBy(RuntimeFeatureContract runtime, IReadOnlySet<string> supportedModelTypes) =>
        Validate(runtime, supportedModelTypes) is FittedModelRejection.None;

    /// <summary>
    /// Whether an inference path reproduces every answer the fitting library recorded.
    /// </summary>
    /// <param name="score">
    /// The runtime's own inference over an observation sequence, or null if it cannot produce one.
    /// </param>
    public bool ReproducesParity(
        Func<IReadOnlyList<IReadOnlyList<double>>, IReadOnlyList<double>?> score)
    {
        ArgumentNullException.ThrowIfNull(score);
        if (ParityChecks.Count == 0) return false;

        foreach (ModelParityCheck check in ParityChecks)
        {
            IReadOnlyList<double>? actual = score(check.Inputs);
            if (actual is null || actual.Count != check.Expected.Count) return false;

            for (int index = 0; index < actual.Count; index++)
            {
                if (!Tolerance.Accepts(actual[index], check.Expected[index])) return false;
            }
        }

        return true;
    }
}
