using QuantDesk.Domain.Contracts;

namespace QuantDesk.Runtime.Experts;

/// <summary>One node of a decision tree: either a split or a leaf.</summary>
/// <param name="SplitFeature">Index into the feature vector, or -1 at a leaf.</param>
/// <param name="Threshold">Values at or below this go left. Meaningless at a leaf.</param>
/// <param name="DefaultLeft">Which way a missing value goes.</param>
/// <param name="Left">Index of the left child, or -1 at a leaf.</param>
/// <param name="Right">Index of the right child, or -1 at a leaf.</param>
/// <param name="LeafValue">The contribution, at a leaf.</param>
public readonly record struct TreeNode(
    int SplitFeature, double Threshold, bool DefaultLeft, int Left, int Right, double LeafValue)
{
    public bool IsLeaf => SplitFeature < 0;
}

/// <summary>A flattened decision tree. Node 0 is the root.</summary>
public sealed record DecisionTree(IReadOnlyList<TreeNode> Nodes);

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
/// number to the last bit. There is nothing to approximate.
///
/// Where a port silently diverges
/// ------------------------------
/// Three places, none of which throws when it is wrong. The comparison at a split is "less than or
/// equal goes left", and getting the boundary backwards moves only the inputs that land exactly on
/// a threshold -- rare, and impossible to notice in aggregate. Missing values follow a per-node
/// default direction rather than a global rule. And the ensemble's output may need a link function
/// applied after summing, so a model whose objective is not identity produces plausible numbers on
/// the wrong scale.
///
/// The parity vectors in the artifact are what makes any of that safe. Each is a real input the
/// fitting process scored, and the loader refuses the model unless this code reproduces the answer.
///
/// What it refuses
/// ---------------
/// Categorical splits, because LightGBM encodes those as bitset membership rather than a threshold
/// and reproducing the encoding is a separate exercise in matching another library's internals.
/// Linear-tree leaves, which hold a regression rather than a constant. Objectives other than plain
/// regression, because each carries its own link. In every case the refusal is the point: a model
/// this code cannot reproduce exactly should not be scoring trades.
/// </summary>
public sealed class GradientBoostedTreeModel
{
    /// <summary>Model types this inference path reproduces.</summary>
    public static readonly IReadOnlySet<string> SupportedModelTypes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "lightgbm", "gbdt" };

    /// <summary>Objectives whose ensemble output needs no link applied after summing.</summary>
    public static readonly IReadOnlySet<string> SupportedObjectives =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "regression", "regression_l2", "l2" };

    private const string ObjectiveKey = "objective";
    private const string CategoricalKey = "has_categorical_splits";
    private const string LinearTreeKey = "linear_tree";

    private readonly FittedModelContract? _artifact;
    private readonly IReadOnlyList<DecisionTree> _trees;
    private readonly double _baseScore;
    private readonly int _featureCount;

    private GradientBoostedTreeModel(
        FittedModelContract? artifact,
        IReadOnlyList<DecisionTree> trees,
        double baseScore,
        int featureCount)
    {
        _artifact = artifact;
        _trees = trees;
        _baseScore = baseScore;
        _featureCount = featureCount;
    }

    public static GradientBoostedTreeModel Unfitted() => new(null, [], 0d, 0);

    public static bool TryLoad(
        FittedModelContract artifact,
        IReadOnlyList<DecisionTree> trees,
        double baseScore,
        int featureCount,
        string runtimeFeatureSchemaHash,
        out GradientBoostedTreeModel model,
        out FittedModelRejection rejection)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(trees);
        model = Unfitted();

        rejection = artifact.Validate(runtimeFeatureSchemaHash, SupportedModelTypes);
        if (rejection is not FittedModelRejection.None) return false;

        if (trees.Count == 0 || featureCount <= 0)
        {
            rejection = FittedModelRejection.UnusableParameters;
            return false;
        }

        // Each of these is a model shape this code does not reproduce. Loading one anyway would
        // produce plausible numbers on the wrong scale or from the wrong branch.
        if (Flag(artifact, CategoricalKey) || Flag(artifact, LinearTreeKey))
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

        foreach (DecisionTree tree in trees)
        {
            if (!IsWellFormed(tree, featureCount))
            {
                rejection = FittedModelRejection.UnusableParameters;
                return false;
            }
        }

        var candidate = new GradientBoostedTreeModel(artifact, trees, baseScore, featureCount);

        // The only thing standing between a hand-written traversal and a silent boundary bug.
        if (!artifact.ReproducesParity(candidate.Predict))
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
    /// The ensemble's prediction: the base score plus every tree's leaf.
    /// </summary>
    public double? Predict(IReadOnlyList<double> features)
    {
        if (_artifact is null) return null;
        ArgumentNullException.ThrowIfNull(features);
        if (features.Count != _featureCount) return null;

        double total = _baseScore;
        foreach (DecisionTree tree in _trees)
        {
            double? leaf = Traverse(tree, features);
            if (leaf is not { } value) return null;
            total += value;
        }

        return double.IsFinite(total) ? total : null;
    }

    private static double? Traverse(DecisionTree tree, IReadOnlyList<double> features)
    {
        int index = 0;

        // Bounded by the node count: a malformed tree with a cycle would otherwise spin forever on
        // the trading path, and a decision loop is not a place to discover that.
        for (int step = 0; step <= tree.Nodes.Count; step++)
        {
            TreeNode node = tree.Nodes[index];
            if (node.IsLeaf) return node.LeafValue;

            double value = features[node.SplitFeature];

            // A non-finite feature is LightGBM's missing value, and each node carries its own
            // direction for it rather than the model carrying one rule.
            index = !double.IsFinite(value)
                ? (node.DefaultLeft ? node.Left : node.Right)

                // Less than or equal goes left. Getting this boundary backwards moves only inputs
                // landing exactly on a threshold -- rare enough to survive every test that is not
                // a parity check.
                : value <= node.Threshold ? node.Left : node.Right;

            if (index < 0 || index >= tree.Nodes.Count) return null;
        }

        return null;
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
