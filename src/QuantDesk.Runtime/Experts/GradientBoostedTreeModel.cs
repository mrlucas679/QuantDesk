using QuantDesk.Domain.Contracts;

namespace QuantDesk.Runtime.Experts;

/// <summary>
/// Inference for a gradient-boosted tree ensemble fitted by LightGBM.
///
/// Why a reimplementation is exact here
/// ------------------------------------
/// An earlier note in this codebase said a boosted ensemble could not cross the language boundary.
/// That was wrong. Training a GBDT is a search -- histogram construction, split gain, early
/// stopping -- and none of that belongs anywhere near a trading path. Scoring one is not a search:
/// walk each tree from root to leaf comparing one feature against one threshold, and add the leaf
/// values. Given the same trees and the same input, two correct implementations produce the same
/// number. Measured against a fitted booster across 114 boundary probes on each of LightGBM's three
/// missing-value conventions, the agreement is exact -- not close, identical.
///
/// The missing-value rule this used to get wrong
/// ---------------------------------------------
/// This traversal sent every non-finite feature down the node's default branch. LightGBM does not
/// work that way, and the branch it takes depends on the node's own missing_type:
///
///   None -- the model was fitted without missing values, so a NaN is converted to zero and
///           compared against the threshold like any other number. It does *not* take the default.
///   NaN  -- a NaN is missing and takes the default branch. Zero is an ordinary value.
///   Zero -- a NaN, and anything within the zero bound, is missing and takes the default branch,
///           which makes an ordinary 0.0 a missing value.
///
/// Measured on a real booster with missing_type None: the old rule scored 2.369 where the booster
/// scored 5.070. It did not throw and it did not look wrong; it scored a different leaf. Only one
/// of the three conventions was ever right, which is why the rule now lives on the node rather than
/// in this method's opinion.
///
/// The zero bound is a float
/// -------------------------
/// LightGBM's literal is 1e-35f, which widens to 1.0000000180025095e-35 rather than to the double
/// 1e-35, and values between the two route differently. It is carried on the artifact rather than
/// written here, because it is a property of the library that produced the trees.
///
/// Where else a port silently diverges
/// -----------------------------------
/// The comparison at a split is "less than or equal goes left", and getting that backwards moves
/// only the inputs landing exactly on a threshold -- rare, and impossible to notice in aggregate.
/// And the ensemble's output may need a link applied after summing, so a model whose objective is
/// not identity produces plausible numbers on the wrong scale.
///
/// The parity vectors are what makes any of that safe. Each is a real input the fitting library
/// scored, and the loader refuses the model unless this code reproduces the answer.
///
/// What it refuses
/// ---------------
/// Categorical splits, because LightGBM encodes those as bitset membership rather than a threshold.
/// Linear-tree leaves, which hold a regression rather than a constant. Random-forest mode, which
/// averages rather than sums. Objectives other than plain regression, because each carries its own
/// link. In every case the refusal is the point: a model this code cannot reproduce exactly should
/// not be scoring trades.
/// </summary>
public sealed class GradientBoostedTreeModel
{
    /// <summary>Model types this inference path reproduces.</summary>
    public static readonly IReadOnlySet<string> SupportedModelTypes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "lightgbm", "gbdt" };

    /// <summary>Objectives whose ensemble output needs no link applied after summing.</summary>
    public static readonly IReadOnlySet<string> SupportedObjectives =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "regression", "regression_l2", "l2", "mean_squared_error", "mse",
        };

    private const string ObjectiveKey = "objective";
    private const string CategoricalKey = "has_categorical_splits";
    private const string LinearTreeKey = "linear_tree";
    private const string AverageOutputKey = "average_output";
    private const string FeatureCountKey = "feature_count";

    private readonly FittedModelContract? _artifact;
    private readonly IReadOnlyList<DecisionTree> _trees;
    private readonly double _zeroThreshold;
    private readonly int _featureCount;

    private GradientBoostedTreeModel(
        FittedModelContract? artifact,
        IReadOnlyList<DecisionTree> trees,
        double zeroThreshold,
        int featureCount)
    {
        _artifact = artifact;
        _trees = trees;
        _zeroThreshold = zeroThreshold;
        _featureCount = featureCount;
    }

    public static GradientBoostedTreeModel Unfitted() => new(null, [], 0d, 0);

    /// <summary>
    /// Loads an artifact, or refuses it with a reason.
    ///
    /// The trees come from the artifact. They used to arrive as a separate argument, which meant
    /// the artifact hash covered everything except the data that decides the answer.
    /// </summary>
    public static bool TryLoad(
        FittedModelContract artifact,
        string runtimeFeatureSchemaHash,
        out GradientBoostedTreeModel model,
        out FittedModelRejection rejection)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        model = Unfitted();

        rejection = artifact.Validate(runtimeFeatureSchemaHash, SupportedModelTypes);
        if (rejection is not FittedModelRejection.None) return false;

        if (!artifact.Parameters.TryGetValue(FeatureCountKey, out double rawFeatureCount)
            || rawFeatureCount < 1d
            || Math.Abs(rawFeatureCount - Math.Round(rawFeatureCount)) > 1e-9d)
        {
            rejection = FittedModelRejection.UnusableParameters;
            return false;
        }

        int featureCount = (int)Math.Round(rawFeatureCount);
        if (artifact.Trees.Count == 0)
        {
            rejection = FittedModelRejection.UnusableParameters;
            return false;
        }

        // Each of these is a model shape this code does not reproduce. Loading one anyway would
        // produce plausible numbers on the wrong scale or from the wrong branch.
        if (Flag(artifact, CategoricalKey)
            || Flag(artifact, LinearTreeKey)
            || Flag(artifact, AverageOutputKey))
        {
            rejection = FittedModelRejection.UnsupportedModelVariant;
            return false;
        }

        string objective = artifact.Variant.TryGetValue(ObjectiveKey, out string? value)
            ? value
            : string.Empty;
        if (!SupportedObjectives.Contains(objective))
        {
            rejection = FittedModelRejection.UnsupportedModelVariant;
            return false;
        }

        foreach (DecisionTree tree in artifact.Trees)
        {
            if (!IsWellFormed(tree, featureCount))
            {
                rejection = FittedModelRejection.UnusableParameters;
                return false;
            }
        }

        var candidate = new GradientBoostedTreeModel(
            artifact, artifact.Trees, artifact.ZeroThreshold, featureCount);

        // The only thing standing between a hand-written traversal and a silent routing bug.
        if (!artifact.ReproducesParity(candidate.ScoreForParity))
        {
            rejection = FittedModelRejection.ParityCheckFailed;
            return false;
        }

        model = candidate;
        return true;
    }

    public bool IsFitted => _artifact is not null;

    public int TreeCount => _trees.Count;

    /// <summary>
    /// The ensemble's prediction: every tree's leaf, summed.
    ///
    /// There is no separate base score. LightGBM folds the initial value into the first tree, and
    /// the sum of the dumped leaves reproduces <c>Booster.predict(raw_score=True)</c> exactly. An
    /// added base term would be a second scoring rule invented here rather than read from the fit.
    /// </summary>
    public double? Predict(IReadOnlyList<double> features)
    {
        if (_artifact is null) return null;
        ArgumentNullException.ThrowIfNull(features);
        if (features.Count != _featureCount) return null;

        double total = 0d;
        foreach (DecisionTree tree in _trees)
        {
            double? leaf = Traverse(tree, features);
            if (leaf is not { } value) return null;
            total += value;
        }

        return double.IsFinite(total) ? total : null;
    }

    private IReadOnlyList<double>? ScoreForParity(IReadOnlyList<IReadOnlyList<double>> inputs)
    {
        // A stateless model, so a parity case is one observation. More than one would mean the
        // fitting side thinks this model carries state, which it does not.
        if (inputs.Count != 1) return null;
        return Predict(inputs[0]) is { } value ? [value] : null;
    }

    private double? Traverse(DecisionTree tree, IReadOnlyList<double> features)
    {
        int index = 0;

        // Bounded by the node count: a malformed tree with a cycle would otherwise spin forever on
        // the trading path, and a decision loop is not a place to discover that.
        for (int step = 0; step <= tree.Nodes.Count; step++)
        {
            TreeNode node = tree.Nodes[index];
            if (node.IsLeaf) return node.LeafValue;

            index = GoesLeft(node, features[node.SplitFeature], _zeroThreshold)
                ? node.Left
                : node.Right;

            if (index < 0 || index >= tree.Nodes.Count) return null;
        }

        return null;
    }

    /// <summary>
    /// Whether a value goes left at a node, by LightGBM's rules.
    ///
    /// The order matters: whether the value counts as missing is decided by the node's convention
    /// *before* any comparison happens, and only then does an ordinary value meet the threshold.
    /// </summary>
    public static bool GoesLeft(TreeNode node, double value, double zeroThreshold)
    {
        bool isNaN = double.IsNaN(value);

        switch (node.MissingType)
        {
            case TreeMissingType.NaN:
                if (isNaN) return node.DefaultLeft;
                break;

            case TreeMissingType.Zero:
                if (isNaN || Math.Abs(value) <= zeroThreshold) return node.DefaultLeft;
                break;

            case TreeMissingType.None:
            default:
                // The model never saw a missing value, so a NaN is not routed to a default branch.
                // LightGBM converts it to zero and compares it like anything else.
                if (isNaN) value = 0d;
                break;
        }

        // Less than or equal goes left. Getting this boundary backwards moves only inputs landing
        // exactly on a threshold -- rare enough to survive every test that is not a parity check,
        // which is why the probes feed each tree's own thresholds back in.
        return value <= node.Threshold;
    }

    private static bool IsWellFormed(DecisionTree tree, int featureCount)
    {
        if (tree.Nodes.Count == 0) return false;

        foreach (TreeNode node in tree.Nodes)
        {
            if (node.IsLeaf)
            {
                if (!double.IsFinite(node.LeafValue)) return false;
                continue;
            }

            if (node.SplitFeature >= featureCount) return false;
            if (!double.IsFinite(node.Threshold)) return false;
            if (node.Left < 0 || node.Left >= tree.Nodes.Count) return false;
            if (node.Right < 0 || node.Right >= tree.Nodes.Count) return false;
        }

        return true;
    }

    private static bool Flag(FittedModelContract artifact, string key) =>
        artifact.Variant.TryGetValue(key, out string? value)
        && bool.TryParse(value, out bool parsed)
        && parsed;
}
