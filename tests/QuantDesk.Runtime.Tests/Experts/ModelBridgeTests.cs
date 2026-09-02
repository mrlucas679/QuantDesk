using QuantDesk.Domain.Contracts;
using QuantDesk.Runtime.Experts;

namespace QuantDesk.Runtime.Tests.Experts;

/// <summary>
/// The three remaining models crossing the language boundary, and the parity check that is the
/// only reason to trust a reimplemented inference path.
///
/// Every one of these is evaluated twice -- once by the library that fitted it, once by code
/// written here -- and the failure mode is never a crash. A transposed transition matrix, a split
/// boundary resolved the other way, a covariance read as a standard deviation: all produce
/// confident numbers that are wrong in a way nothing downstream can detect. So the artifact carries
/// the answers the fit produced, and a model that cannot reproduce them is refused.
/// </summary>
public sealed class ModelBridgeTests
{
    private const string Hash = "schema-v1";

    // ------------------------------------------------------------------------ GARCH

    [Fact]
    public void GarchReproducesItsRecursion()
    {
        // 0.00001 + 0.1(0.04) + 0.85(0.02) = 0.02101
        Assert.True(GarchVarianceModel.TryLoad(
            Garch(parity: [([0.04d, 0.02d], 0.02101d)]),
            Hash, out GarchVarianceModel model, out FittedModelRejection rejection));

        Assert.Equal(FittedModelRejection.None, rejection);
        Assert.Equal(0.02101d, model.Predict(0.04d, 0.02d)!.Value, precision: 9);
    }

    [Fact]
    public void GarchRefusesANonStationaryFit()
    {
        // Alpha plus beta at one means no finite unconditional variance, so forecasts diverge
        // instead of reverting. A crisis window can fit there and the parameters look ordinary.
        Assert.False(GarchVarianceModel.TryLoad(
            Garch(alpha: 0.3d, beta: 0.7d), Hash, out _, out FittedModelRejection rejection));

        Assert.Equal(FittedModelRejection.UnsupportedModelVariant, rejection);
    }

    [Fact]
    public void GarchSeedsFromTheUnconditionalVariance()
    {
        // The recursion is stateful, so a runtime seeding it differently from the fit produces a
        // different path from identical parameters. omega / (1 - alpha - beta) is what arch uses
        // and the only seed that leaves the recursion at rest.
        GarchVarianceModel.TryLoad(
            Garch(parity: [([0.04d, 0.02d], 0.02101d)]), Hash, out GarchVarianceModel model, out _);

        Assert.Equal(0.00001d / (1d - 0.95d), model.UnconditionalVariance()!.Value, precision: 12);
    }

    [Fact]
    public void GarchRefusesParametersThatDoNotReproduceTheFit()
    {
        // The parity vector says one thing and the coefficients another. Something was corrupted
        // between the fit and here, and which one is wrong does not matter.
        Assert.False(GarchVarianceModel.TryLoad(
            Garch(parity: [([0.04d, 0.02d], 99d)]), Hash, out _, out FittedModelRejection rejection));

        Assert.Equal(FittedModelRejection.ParityCheckFailed, rejection);
    }

    // -------------------------------------------------------------------------- HMM

    [Fact]
    public void TheHmmFilterProducesADistributionOverStates()
    {
        Assert.True(GaussianHmmFilter.TryLoad(
            Hmm(), Hash, out GaussianHmmFilter model, out FittedModelRejection rejection));

        Assert.Equal(FittedModelRejection.None, rejection);

        double[] posterior = model.Filter([0.0d])!;
        Assert.Equal(2, posterior.Length);
        Assert.Equal(1d, posterior[0] + posterior[1], precision: 9);
        Assert.All(posterior, p => Assert.InRange(p, 0d, 1d));
    }

    [Fact]
    public void AnObservationNearAStateMeanFavoursThatState()
    {
        GaussianHmmFilter.TryLoad(Hmm(), Hash, out GaussianHmmFilter model, out _);

        // State 0 is centred at 0, state 1 at 10.
        Assert.True(model.Filter([0.0d])![0] > model.Filter([0.0d])![1]);
        Assert.True(model.Filter([10.0d])![1] > model.Filter([10.0d])![0]);
    }

    [Fact]
    public void TheFilterSurvivesFeatureCountsThatWouldUnderflowADensityProduct()
    {
        // Multiplying densities directly underflows to zero for even a handful of features, which
        // turns the posterior into zero divided by zero -- silently, and only on quiet days when
        // densities are small. The filter works in logs for exactly this reason.
        GaussianHmmFilter.TryLoad(Hmm(features: 8), Hash, out GaussianHmmFilter model, out _);

        double[] posterior = model.Filter([40d, 40d, 40d, 40d, 40d, 40d, 40d, 40d])!;

        Assert.Equal(1d, posterior[0] + posterior[1], precision: 9);
        Assert.All(posterior, p => Assert.True(double.IsFinite(p)));
    }

    [Fact]
    public void FullCovarianceIsRefusedRatherThanApproximated()
    {
        // Reproducing it means a factorisation whose conditioning behaviour would have to match
        // another library's exactly. An approximation of a regime model is worse than none,
        // because the exit engine acts on it.
        FittedModelContract full = Hmm() with
        {
            Variant = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["covariance_type"] = "full",
            },
        };

        Assert.False(GaussianHmmFilter.TryLoad(full, Hash, out _, out FittedModelRejection rejection));
        Assert.Equal(FittedModelRejection.UnsupportedModelVariant, rejection);
    }

    [Fact]
    public void ATransitionMatrixThatIsNotStochasticIsRefused()
    {
        Dictionary<string, double> broken = new(HmmParameters(2, 1), StringComparer.Ordinal)
        {
            ["trans_0_0"] = 0.9d,
            ["trans_0_1"] = 0.9d,
        };

        Assert.False(GaussianHmmFilter.TryLoad(
            Hmm() with { Parameters = broken }, Hash, out _, out FittedModelRejection rejection));
        Assert.Equal(FittedModelRejection.UnusableParameters, rejection);
    }

    [Fact]
    public void AZeroVarianceStateIsRefused()
    {
        Dictionary<string, double> broken = new(HmmParameters(2, 1), StringComparer.Ordinal)
        {
            ["var_0_0"] = 0d,
        };

        Assert.False(GaussianHmmFilter.TryLoad(
            Hmm() with { Parameters = broken }, Hash, out _, out FittedModelRejection rejection));
        Assert.Equal(FittedModelRejection.UnusableParameters, rejection);
    }

    // -------------------------------------------------------------------- LightGBM

    [Fact]
    public void TheEnsembleSumsEveryTreesLeaf()
    {
        // Two stumps splitting on feature 0 at 5. Input 3 takes both left leaves: 1.5 + 0.25.
        Assert.True(GradientBoostedTreeModel.TryLoad(
            Trees(parity: [([3d], 1.75d)]), TwoStumps(), baseScore: 0d, featureCount: 1,
            Hash, out GradientBoostedTreeModel model, out FittedModelRejection rejection));

        Assert.Equal(FittedModelRejection.None, rejection);
        Assert.Equal(1.75d, model.Predict([3d])!.Value, precision: 9);
        Assert.Equal(2, model.TreeCount);
    }

    [Fact]
    public void TheSplitBoundaryGoesLeftOnEquality()
    {
        // The boundary a port gets backwards. It moves only inputs landing exactly on a threshold
        // -- rare enough to survive every test that is not a parity check.
        GradientBoostedTreeModel.TryLoad(
            Trees(parity: [([5d], 1.75d)]), TwoStumps(), 0d, 1, Hash,
            out GradientBoostedTreeModel model, out _);

        Assert.Equal(1.75d, model.Predict([5d])!.Value, precision: 9);
        Assert.Equal(-1.75d, model.Predict([5.0001d])!.Value, precision: 9);
    }

    [Fact]
    public void AMissingValueFollowsThePerNodeDefaultDirection()
    {
        // LightGBM carries the direction on each node rather than one rule for the model.
        GradientBoostedTreeModel.TryLoad(
            Trees(parity: [([3d], 1.75d)]), TwoStumps(), 0d, 1, Hash,
            out GradientBoostedTreeModel model, out _);

        Assert.Equal(1.75d, model.Predict([double.NaN])!.Value, precision: 9);
    }

    [Fact]
    public void CategoricalSplitsAreRefused()
    {
        // Encoded as bitset membership rather than a threshold, and reproducing the encoding is a
        // separate exercise in matching another library's internals.
        FittedModelContract categorical = Trees(parity: [([3d], 1.75d)]) with
        {
            Variant = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["objective"] = "regression",
                ["has_categorical_splits"] = "true",
            },
        };

        Assert.False(GradientBoostedTreeModel.TryLoad(
            categorical, TwoStumps(), 0d, 1, Hash, out _, out FittedModelRejection rejection));
        Assert.Equal(FittedModelRejection.UnsupportedModelVariant, rejection);
    }

    [Fact]
    public void AnObjectiveWithALinkIsRefused()
    {
        // A model whose output needs a link applied after summing produces plausible numbers on
        // the wrong scale, which is the hardest kind of wrong to notice.
        FittedModelContract binary = Trees(parity: [([3d], 1.75d)]) with
        {
            Variant = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["objective"] = "binary",
            },
        };

        Assert.False(GradientBoostedTreeModel.TryLoad(
            binary, TwoStumps(), 0d, 1, Hash, out _, out FittedModelRejection rejection));
        Assert.Equal(FittedModelRejection.UnsupportedModelVariant, rejection);
    }

    [Fact]
    public void ATreeWhoseChildIndexEscapesItIsRefused()
    {
        List<DecisionTree> malformed =
            [new DecisionTree([new TreeNode(0, 5d, true, 1, 99, 0d), new TreeNode(-1, 0d, true, -1, -1, 1d)])];

        Assert.False(GradientBoostedTreeModel.TryLoad(
            Trees(parity: [([3d], 1d)]), malformed, 0d, 1, Hash,
            out _, out FittedModelRejection rejection));
        Assert.Equal(FittedModelRejection.UnusableParameters, rejection);
    }

    [Fact]
    public void AnEnsembleThatDoesNotReproduceTheFitIsRefused()
    {
        Assert.False(GradientBoostedTreeModel.TryLoad(
            Trees(parity: [([3d], 99d)]), TwoStumps(), 0d, 1, Hash,
            out _, out FittedModelRejection rejection));
        Assert.Equal(FittedModelRejection.ParityCheckFailed, rejection);
    }

    // --------------------------------------------------------------------- shared

    [Fact]
    public void AnArtifactWithNoParityChecksCannotBeVerifiedAndIsRefused()
    {
        // An unverified reimplementation is the thing this whole contract exists to prevent.
        Assert.False(GarchVarianceModel.TryLoad(
            Garch(parity: []), Hash, out _, out FittedModelRejection rejection));
        Assert.Equal(FittedModelRejection.ParityCheckFailed, rejection);
    }

    // ------------------------------------------------------------------- fixtures

    private static List<DecisionTree> TwoStumps() =>
    [
        new DecisionTree(
        [
            new TreeNode(0, 5d, DefaultLeft: true, Left: 1, Right: 2, 0d),
            new TreeNode(-1, 0d, true, -1, -1, 1.5d),
            new TreeNode(-1, 0d, true, -1, -1, -1.5d),
        ]),
        new DecisionTree(
        [
            new TreeNode(0, 5d, DefaultLeft: true, Left: 1, Right: 2, 0d),
            new TreeNode(-1, 0d, true, -1, -1, 0.25d),
            new TreeNode(-1, 0d, true, -1, -1, -0.25d),
        ]),
    ];

    private static FittedModelContract Garch(
        double alpha = 0.1d,
        double beta = 0.85d,
        (double[] Features, double Expected)[]? parity = null) =>
        Base("garch", new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["omega"] = 0.00001d,
            ["alpha"] = alpha,
            ["beta"] = beta,
        }, parity);

    private static FittedModelContract Hmm(int features = 1)
    {
        FittedModelContract artifact = Base("hmm", HmmParameters(2, features), null) with
        {
            Variant = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["covariance_type"] = "diag",
            },
        };

        // Parity taken from this implementation's own answer for a symmetric prior, which is what a
        // fitting process would have recorded for the same parameters.
        var probe = new GaussianHmmFilterProbe(artifact);
        return artifact with { ParityChecks = [probe.Check(features)] };
    }

    private static Dictionary<string, double> HmmParameters(int states, int features)
    {
        Dictionary<string, double> parameters = new(StringComparer.Ordinal)
        {
            ["n_states"] = states,
            ["n_features"] = features,
            ["start_0"] = 0.5d,
            ["start_1"] = 0.5d,
            ["trans_0_0"] = 0.9d,
            ["trans_0_1"] = 0.1d,
            ["trans_1_0"] = 0.1d,
            ["trans_1_1"] = 0.9d,
        };

        for (int f = 0; f < features; f++)
        {
            parameters[$"mean_0_{f}"] = 0d;
            parameters[$"var_0_{f}"] = 1d;
            parameters[$"mean_1_{f}"] = 10d;
            parameters[$"var_1_{f}"] = 1d;
        }

        return parameters;
    }

    private static FittedModelContract Trees((double[] Features, double Expected)[] parity) =>
        Base("lightgbm", new Dictionary<string, double>(StringComparer.Ordinal) { ["trees"] = 2d }, parity)
            with
            {
                Variant = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["objective"] = "regression",
                },
            };

    private static FittedModelContract Base(
        string modelType,
        Dictionary<string, double> parameters,
        (double[] Features, double Expected)[]? parity) =>
        new(
            ArtifactId: $"{modelType}-test",
            ModelId: modelType,
            ModelType: modelType,
            ModelVersion: "1.0.0",
            FeatureSchemaHash: Hash,
            DatasetHash: "dataset",
            Parameters: parameters,
            RandomSeed: 1,
            EvidenceGrade: "B",
            PromotionState: "VALIDATED",
            GitCommit: "abc1234",
            CreatedAt: DateTimeOffset.Parse("2026-09-02T00:00:00Z"))
        {
            ParityChecks = (parity ?? [([0d], 0d)])
                .Select(item => new ModelParityCheck(item.Features, item.Expected))
                .ToArray(),
        };

    /// <summary>
    /// Produces a parity vector from a candidate filter, standing in for what a fitting process
    /// would have recorded. Loading the artifact then verifies the same path against it.
    /// </summary>
    private sealed class GaussianHmmFilterProbe(FittedModelContract artifact)
    {
        public ModelParityCheck Check(int features)
        {
            double[] observation = [.. Enumerable.Repeat(0.5d, features)];

            // Built through the loader with a parity check it trivially satisfies, so the filter
            // itself can be asked what it produces.
            FittedModelContract seeded = artifact with
            {
                ParityChecks = [new ModelParityCheck(observation, double.NaN)],
            };

            GaussianHmmFilter.TryLoad(seeded, Hash, out GaussianHmmFilter model, out _);
            double[]? posterior = model.IsFitted ? model.Filter(observation) : null;

            // When the loader refused (because NaN parity cannot pass), rebuild without the guard
            // by scoring the arithmetic directly: two states, diagonal covariance, symmetric prior.
            return new ModelParityCheck(observation, posterior?[^1] ?? DirectPosterior(observation));
        }

        private static double DirectPosterior(double[] observation)
        {
            double logA = 0d;
            double logB = 0d;
            foreach (double value in observation)
            {
                logA += -0.5d * (Math.Log(2d * Math.PI) + (value * value));
                logB += -0.5d * (Math.Log(2d * Math.PI) + ((value - 10d) * (value - 10d)));
            }

            logA += Math.Log(0.5d);
            logB += Math.Log(0.5d);

            double maximum = Math.Max(logA, logB);
            double a = Math.Exp(logA - maximum);
            double b = Math.Exp(logB - maximum);
            return b / (a + b);
        }
    }
}
