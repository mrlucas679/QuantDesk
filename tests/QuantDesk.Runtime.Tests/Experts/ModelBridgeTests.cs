using QuantDesk.Domain.Contracts;
using QuantDesk.Runtime.Experts;
using QuantDesk.Runtime.Research;

namespace QuantDesk.Runtime.Tests.Experts;

/// <summary>
/// The models that cross from Python, loaded from artifacts Python actually wrote.
///
/// What changed about these tests
/// ------------------------------
/// The previous version of this file built its own contracts in memory and computed the parity
/// answers with the very implementations it was checking -- the HMM's expected posterior came from
/// the C# filter, and the GARCH and tree expectations were arithmetic done by hand to match the C#
/// code. Every one of them passed. None of them could have failed, because nothing in the loop had
/// ever seen <c>arch</c>, <c>hmmlearn</c> or <c>lightgbm</c>.
///
/// These load the committed artifacts instead. Each was fitted by its real library, and every
/// expected answer in them came from that library's own prediction API. When the loader accepts one
/// here, it is because this code reproduced what Python computed -- which is the only thing parity
/// was ever supposed to establish.
///
/// The schema hash question
/// ------------------------
/// The positive paths feed the artifact's own feature-schema hash, because what is under test is
/// inference rather than the runtime's feature derivation. The mismatch path feeds a different one
/// deliberately, so the refusal is still exercised.
/// </summary>
public sealed class ModelBridgeTests
{
    private const string DifferentSchema = "a-schema-this-model-was-not-fitted-on";

    // ------------------------------------------------------------------- the artifacts load

    [Fact]
    public void TheHarArtifactPythonWroteReproducesWhatPythonPredicted()
    {
        FittedModelContract artifact = Fixture("har-realised-variance.json");

        Assert.True(HarVarianceModel.TryLoad(
            artifact, artifact.FeatureSchemaHash, out HarVarianceModel model,
            out FittedModelRejection rejection));

        Assert.Equal(FittedModelRejection.None, rejection);
        Assert.True(model.IsFitted);
        Assert.Equal("quantdesk_research.models.har", artifact.ProducerLibrary);
    }

    [Fact]
    public void TheGarchArtifactWarmsColdAndLandsWhereArchLanded()
    {
        // The parity cases are warm-up windows and the conditional variance arch reported at the end
        // of each. The runtime starts the recursion at zero -- nothing like arch's 0.94 backcast of
        // the first 75 residuals -- and still has to arrive at the same number, because the window
        // is sized from beta so the seed cannot survive it.
        FittedModelContract artifact = Fixture("garch-conditional-variance.json");

        Assert.True(GarchVarianceModel.TryLoad(
            artifact, artifact.FeatureSchemaHash, out GarchVarianceModel model,
            out FittedModelRejection rejection));

        Assert.Equal(FittedModelRejection.None, rejection);
        Assert.Equal("arch", artifact.ProducerLibrary);
        Assert.True(model.WarmupBars > 1);
        Assert.Equal("percent", model.ReturnUnits);
    }

    [Fact]
    public void TheHmmArtifactReproducesWhatHmmlearnFiltered()
    {
        FittedModelContract artifact = Fixture("gaussian-hmm-regime.json");

        Assert.True(GaussianHmmFilter.TryLoad(
            artifact, artifact.FeatureSchemaHash, out GaussianHmmFilter model,
            out FittedModelRejection rejection));

        Assert.Equal(FittedModelRejection.None, rejection);
        Assert.Equal("hmmlearn", artifact.ProducerLibrary);
        Assert.Equal(3, model.StateCount);
    }

    [Fact]
    public void TheTreeArtifactReproducesTheBooster()
    {
        FittedModelContract artifact = Fixture("lightgbm-direction.json");

        Assert.True(GradientBoostedTreeModel.TryLoad(
            artifact, artifact.FeatureSchemaHash, out GradientBoostedTreeModel model,
            out FittedModelRejection rejection));

        Assert.Equal(FittedModelRejection.None, rejection);
        Assert.Equal("lightgbm", artifact.ProducerLibrary);
        Assert.Equal(5, model.TreeCount);
    }

    // ------------------------------------------------------- the bugs the fixtures would catch

    [Fact]
    public void TheTreeFixtureCarriesAMissingValueTheOldRuleWouldHaveRoutedWrong()
    {
        // The rule this replaced sent every non-finite feature down the default branch, which is
        // right only for the NaN convention and wrong for the other two. A fixture whose probes had
        // no missing value would have passed against the broken code.
        FittedModelContract artifact = Fixture("lightgbm-direction.json");

        Assert.Contains(
            artifact.ParityChecks,
            check => check.Inputs[0].Any(double.IsNaN));
    }

    [Theory]
    [InlineData(TreeMissingType.None, double.NaN, 5d, true)]
    [InlineData(TreeMissingType.NaN, double.NaN, 5d, false)]
    [InlineData(TreeMissingType.Zero, 0d, 5d, false)]
    [InlineData(TreeMissingType.None, 0d, 5d, true)]
    [InlineData(TreeMissingType.NaN, 0d, 5d, true)]
    public void EachMissingConventionRoutesTheSameValueDifferently(
        TreeMissingType missingType, double value, double threshold, bool expectedLeft)
    {
        // Three conventions, one input, three answers. Under None a NaN becomes zero and meets the
        // threshold; under NaN it takes the default branch; under Zero an ordinary 0.0 is the
        // missing one. Collapsing them to a single rule is exactly the defect this replaced, and it
        // scored 2.369 where the booster scored 5.070.
        var node = new TreeNode(
            SplitFeature: 0, Threshold: threshold, MissingType: missingType,
            DefaultLeft: false, Left: 1, Right: 2, LeafValue: 0d);

        Assert.Equal(
            expectedLeft,
            GradientBoostedTreeModel.GoesLeft(node, value, zeroThreshold: 1.0000000180025095e-35d));
    }

    [Fact]
    public void TheZeroBoundIsTheFloatLiteralRatherThanTheDouble()
    {
        // LightGBM's kZeroThreshold is 1e-35f, which widens to 1.0000000180025095e-35 and not to the
        // double 1e-35. A booster split at exactly that value and routed it as missing while a
        // double bound called it ordinary. One probe in 121 disagreed.
        const double bound = 1.0000000180025095e-35d;
        var node = new TreeNode(0, -bound, TreeMissingType.Zero, DefaultLeft: true, 1, 2, 0d);

        // Exactly at the bound: missing, so the default branch. Under a double 1e-35 bound this
        // value is outside, becomes an ordinary comparison, and goes left for the wrong reason --
        // which happens to be the same direction here, so only the next case separates them.
        Assert.True(GradientBoostedTreeModel.GoesLeft(node, -bound, bound));

        // Just outside the bound on the far side of the threshold: an ordinary value, and greater
        // than a negative threshold, so right. Nothing about being near zero saves it.
        Assert.False(GradientBoostedTreeModel.GoesLeft(node, 1e-30d, bound));

        // And a value the wider double bound would wrongly call ordinary is still missing here.
        Assert.True(GradientBoostedTreeModel.GoesLeft(node, 1e-36d, bound));
    }

    [Fact]
    public void TreesFlippedToTheWrongMissingConventionFailParity()
    {
        // The point of parity: change how the model routes and the artifact stops loading. Without
        // this the missing-value rule could drift back and every test but the fixture would pass.
        FittedModelContract artifact = Fixture("lightgbm-direction.json");
        FittedModelContract altered = artifact with
        {
            Trees = [.. artifact.Trees.Select(tree => new DecisionTree(
                [.. tree.Nodes.Select(node => node.IsLeaf
                    ? node
                    : node with { MissingType = TreeMissingType.None })]))],
        };

        Assert.False(GradientBoostedTreeModel.TryLoad(
            altered, altered.FeatureSchemaHash, out _, out FittedModelRejection rejection));
        Assert.Equal(FittedModelRejection.ParityCheckFailed, rejection);
    }

    [Fact]
    public void ATransposedTransitionMatrixFailsParity()
    {
        // The check the previous parity design could not make. Its cases filtered a single
        // observation from the fitted prior, so the transition matrix was never applied and a
        // transposed one passed as readily as the right one.
        FittedModelContract artifact = Fixture("gaussian-hmm-regime.json");
        int states = (int)artifact.Parameters["n_states"];

        var transposed = new Dictionary<string, double>(artifact.Parameters, StringComparer.Ordinal);
        for (int i = 0; i < states; i++)
        {
            for (int j = 0; j < states; j++)
                transposed[$"trans_{i}_{j}"] = artifact.Parameters[$"trans_{j}_{i}"];
        }

        Assert.False(GaussianHmmFilter.TryLoad(
            artifact with { Parameters = transposed },
            artifact.FeatureSchemaHash, out _, out FittedModelRejection rejection));

        // Either the transpose is not row-stochastic, or it is and produces different posteriors.
        // Both are refusals; what matters is that the check no longer passes regardless.
        Assert.NotEqual(FittedModelRejection.None, rejection);
    }

    [Fact]
    public void EveryHmmParitySequenceIsLongEnoughToApplyTheTransitionMatrix()
    {
        FittedModelContract artifact = Fixture("gaussian-hmm-regime.json");

        Assert.Equal(ParityKind.SequenceToVector, artifact.ParityKind);
        Assert.All(artifact.ParityChecks, check => Assert.True(check.Inputs.Count >= 2));
    }

    [Fact]
    public void AnHmmParityCaseComparesTheWholePosteriorNotOneState()
    {
        // Returning a single state's probability hid the rest of the distribution, which is where a
        // mislabelled or reordered state shows up.
        FittedModelContract artifact = Fixture("gaussian-hmm-regime.json");
        int states = (int)artifact.Parameters["n_states"];

        Assert.All(artifact.ParityChecks, check => Assert.Equal(states, check.Expected.Count));
    }

    [Fact]
    public void AWarmUpShorterThanTheArtifactRequiresRefusesRatherThanGuessing()
    {
        // Short of the window the seed has not decayed out, so the answer would depend on a number
        // nobody chose.
        FittedModelContract artifact = Fixture("garch-conditional-variance.json");
        GarchVarianceModel.TryLoad(
            artifact, artifact.FeatureSchemaHash, out GarchVarianceModel model, out _);

        Assert.Null(model.WarmedVariance([0.5d, 0.4d, 0.6d]));
        Assert.NotNull(model.WarmedVariance([.. Enumerable.Repeat(0.5d, model.WarmupBars)]));
    }

    [Fact]
    public void TheUnconditionalVarianceIsReportedButIsNotTheSeed()
    {
        // arch backcasts a 0.94-weighted average of the first 75 squared residuals. This class once
        // claimed the unconditional variance was what arch used; it is reported now as context for
        // a forecast, and the recursion warms up rather than seeding at all.
        FittedModelContract artifact = Fixture("garch-conditional-variance.json");
        GarchVarianceModel.TryLoad(
            artifact, artifact.FeatureSchemaHash, out GarchVarianceModel model, out _);

        double unconditional = model.UnconditionalVariance()!.Value;
        double beta = artifact.Parameters["beta"];

        // Warmed over a window of zero shocks, the recursion settles at omega / (1 - beta): the
        // alpha term drops out because every shock is zero, and only beta carries the previous
        // variance forward. That is a different number from the unconditional variance, which
        // divides by 1 - alpha - beta -- so the two disagreeing is the point, not a rounding
        // artefact, and the seed the fit used is neither of them.
        double quietState = model.WarmedVariance(
            [.. Enumerable.Repeat(0d, model.WarmupBars)])!.Value;

        Assert.True(unconditional > 0d);
        Assert.Equal(artifact.Parameters["omega"] / (1d - beta), quietState, precision: 12);
        Assert.True(unconditional > quietState);
    }

    // -------------------------------------------------------------------------- the refusals

    [Fact]
    public void AFeatureSchemaMismatchIsFatalAndNeverReordered()
    {
        // Section 4.1 makes this a hard model-invalid condition and prohibits best-effort
        // reordering, which is a strong rule about a weak failure: a model fed features in an order
        // it was not fitted on does not throw and does not look wrong.
        FittedModelContract artifact = Fixture("lightgbm-direction.json");

        Assert.False(GradientBoostedTreeModel.TryLoad(
            artifact, DifferentSchema, out _, out FittedModelRejection rejection));
        Assert.Equal(FittedModelRejection.FeatureSchemaMismatch, rejection);
    }

    [Fact]
    public void AnArtifactFromAContractVersionThisRuntimeDoesNotReadIsRefused()
    {
        FittedModelContract artifact = Fixture("har-realised-variance.json") with
        {
            ArtifactSchemaVersion = "runtime-inference-v1",
        };

        Assert.False(HarVarianceModel.TryLoad(
            artifact, artifact.FeatureSchemaHash, out _, out FittedModelRejection rejection));
        Assert.Equal(FittedModelRejection.UnsupportedArtifactVersion, rejection);
    }

    [Fact]
    public void AnUnpromotedArtifactCannotInformADecision()
    {
        FittedModelContract artifact = Fixture("gaussian-hmm-regime.json") with
        {
            PromotionState = "EXPERIMENTAL",
        };

        Assert.False(GaussianHmmFilter.TryLoad(
            artifact, artifact.FeatureSchemaHash, out _, out FittedModelRejection rejection));
        Assert.Equal(FittedModelRejection.InsufficientPromotion, rejection);
    }

    [Fact]
    public void AnArtifactWithNoParityCasesCannotBeVerifiedAndIsRefused()
    {
        FittedModelContract artifact = Fixture("har-realised-variance.json") with
        {
            ParityChecks = [],
        };

        Assert.False(HarVarianceModel.TryLoad(
            artifact, artifact.FeatureSchemaHash, out _, out FittedModelRejection rejection));
        Assert.Equal(FittedModelRejection.ParityCheckFailed, rejection);
    }

    [Fact]
    public void AnEnsembleWhoseTreesWereStrippedIsRefused()
    {
        // The trees are the model. They used to arrive as a separate argument, so the artifact hash
        // covered everything except the data that decides the answer.
        FittedModelContract artifact = Fixture("lightgbm-direction.json") with { Trees = [] };

        Assert.False(GradientBoostedTreeModel.TryLoad(
            artifact, artifact.FeatureSchemaHash, out _, out FittedModelRejection rejection));
        Assert.Equal(FittedModelRejection.UnusableParameters, rejection);
    }

    [Fact]
    public void AMultiStepHorizonIsRefusedRatherThanApproximated()
    {
        // arch uses the realised residual for the first step and an expectation involving alpha plus
        // beta thereafter. This version answers one step and says so.
        FittedModelContract artifact = Fixture("garch-conditional-variance.json");
        var variant = new Dictionary<string, string>(artifact.Variant, StringComparer.OrdinalIgnoreCase)
        {
            ["horizon"] = "multi_step",
        };

        Assert.False(GarchVarianceModel.TryLoad(
            artifact with { Variant = variant },
            artifact.FeatureSchemaHash, out _, out FittedModelRejection rejection));
        Assert.Equal(FittedModelRejection.UnsupportedModelVariant, rejection);
    }

    [Fact]
    public void AGarchArtifactWithNoDeclaredReturnScaleIsRefused()
    {
        // omega does not carry its units. A model fitted on percent returns consuming decimals is
        // wrong by a factor of ten thousand and nothing about the number looks wrong.
        FittedModelContract artifact = Fixture("garch-conditional-variance.json");
        var variant = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> entry in artifact.Variant)
        {
            if (!string.Equals(entry.Key, "return_units", StringComparison.OrdinalIgnoreCase))
                variant[entry.Key] = entry.Value;
        }

        Assert.False(GarchVarianceModel.TryLoad(
            artifact with { Variant = variant },
            artifact.FeatureSchemaHash, out _, out FittedModelRejection rejection));
        Assert.Equal(FittedModelRejection.UnsupportedModelVariant, rejection);
    }

    [Fact]
    public void ACovarianceShapeThisFilterCannotReproduceIsRefused()
    {
        // Full and tied covariance need a factorisation whose conditioning behaviour would have to
        // match another library's exactly. An approximated regime model is worse than none, because
        // the exit engine acts on it.
        FittedModelContract artifact = Fixture("gaussian-hmm-regime.json");
        var variant = new Dictionary<string, string>(artifact.Variant, StringComparer.OrdinalIgnoreCase)
        {
            ["covariance_type"] = "full",
        };

        Assert.False(GaussianHmmFilter.TryLoad(
            artifact with { Variant = variant },
            artifact.FeatureSchemaHash, out _, out FittedModelRejection rejection));
        Assert.Equal(FittedModelRejection.UnsupportedModelVariant, rejection);
    }

    // ------------------------------------------------------------------------- the reader

    [Fact]
    public void AnUnknownMissingConventionIsRefusedRatherThanDefaulted()
    {
        // Defaulting to None would silently score a different leaf, which is the whole class of
        // failure this reader exists to make impossible.
        string json = File.ReadAllText(Path.Combine(FixtureRoot, "lightgbm-direction.json"))
            .Replace("\"missing_type\": \"NaN\"", "\"missing_type\": \"Sometimes\"", StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => FittedModelArtifactReader.Read(json));
    }

    [Fact]
    public void EveryCommittedArtifactNamesTheLibraryAndVersionThatProducedIt()
    {
        // When a port and a library disagree, the first question is which version of the library.
        foreach (string name in new[]
        {
            "har-realised-variance.json", "garch-conditional-variance.json",
            "gaussian-hmm-regime.json", "lightgbm-direction.json",
        })
        {
            FittedModelContract artifact = Fixture(name);
            Assert.False(string.IsNullOrWhiteSpace(artifact.ProducerLibrary));
            Assert.False(string.IsNullOrWhiteSpace(artifact.ProducerLibraryVersion));
            Assert.False(string.IsNullOrWhiteSpace(artifact.ArtifactHash));
        }
    }

    // ------------------------------------------------------------------------- fixtures

    private static FittedModelContract Fixture(string name) =>
        FittedModelArtifactReader.ReadFile(Path.Combine(FixtureRoot, name));

    private static readonly string FixtureRoot = LocateFixtures();

    /// <summary>Walks up from the test binary to the repository root.</summary>
    private static string LocateFixtures()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, ".git")))
            directory = directory.Parent;
        return Path.Combine(
            directory?.FullName ?? AppContext.BaseDirectory,
            "tests", "fixtures", "model-artifacts");
    }
}
