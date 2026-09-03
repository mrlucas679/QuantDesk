using QuantDesk.Domain.Contracts;
using QuantDesk.Runtime.Experts;
using QuantDesk.Runtime.Research;

namespace QuantDesk.Runtime.Tests.Experts;

/// <summary>
/// The end-to-end claim: a process that restarts forecasts the same thing it forecast before.
///
/// What this is actually testing
/// -----------------------------
/// Every other test here checks one link. This checks the chain: Python fitted a model, sealed it,
/// and wrote it to disk; a process reads those bytes, verifies it reproduces what the library
/// computed, and forecasts. Then everything is thrown away and it happens again from the same file.
/// The two forecasts must be identical to the bit, and both must equal the number Python recorded.
///
/// Why "identical to the bit" and not "close"
/// ------------------------------------------
/// A restart is not a new experiment. If a forecast moves because the process restarted, then some
/// piece of state survived that should not have -- a recursion seeded from wherever it happened to
/// be, a cached warm-up, an ordering that depended on load sequence -- and the model is no longer a
/// function of the artifact and the inputs. Every one of those is invisible in a running system and
/// obvious here, which is the only reason this test is worth its runtime.
///
/// GARCH is the one that would have failed. It carries a stateful recursion, and the design that
/// nearly shipped -- exporting the fit's terminal variance and continuing from it -- would have made
/// the restarted forecast depend on how stale that state had become. Warming up from cold over a
/// window sized from beta is what makes this pass without a staleness policy existing at all.
/// </summary>
public sealed class ModelRestartTests
{
    [Fact]
    public void TheHarForecastSurvivesARestartAndMatchesWhatPythonRecorded()
    {
        AssertRestartIsIdentical(
            "har-realised-variance.json",
            (artifact, inputs) =>
            {
                Assert.True(HarVarianceModel.TryLoad(artifact, Runtime(artifact), out var model, out _));
                IReadOnlyList<double> features = inputs[0];
                return [model.Predict(features[0], features[1], features[2])!.Value];
            });
    }

    [Fact]
    public void TheGarchForecastSurvivesARestartAndMatchesWhatArchRecorded()
    {
        // The one with state. Nothing is carried across the restart, so the recursion starts cold
        // both times and the warm-up window is what makes the answers agree.
        AssertRestartIsIdentical(
            "garch-conditional-variance.json",
            (artifact, inputs) =>
            {
                Assert.True(GarchVarianceModel.TryLoad(artifact, Runtime(artifact), out var model, out _));
                return [model.WarmedVariance([.. inputs.Select(row => row[0])])!.Value];
            });
    }

    [Fact]
    public void TheRegimePosteriorSurvivesARestartAndMatchesWhatHmmlearnRecorded()
    {
        AssertRestartIsIdentical(
            "gaussian-hmm-regime.json",
            (artifact, inputs) =>
            {
                Assert.True(GaussianHmmFilter.TryLoad(artifact, Runtime(artifact), out var model, out _));
                double[]? posterior = null;
                foreach (IReadOnlyList<double> observation in inputs)
                    posterior = model.Filter(observation, posterior);
                return posterior!;
            });
    }

    [Fact]
    public void TheEnsembleScoreSurvivesARestartAndMatchesWhatTheBoosterRecorded()
    {
        AssertRestartIsIdentical(
            "lightgbm-direction.json",
            (artifact, inputs) =>
            {
                Assert.True(GradientBoostedTreeModel.TryLoad(
                    artifact, Runtime(artifact), out var model, out _));
                return [model.Predict(inputs[0])!.Value];
            });
    }

    [Fact]
    public void ARestartThatCannotReadTheFileForecastsNothingRatherThanSomethingStale()
    {
        // The failure mode a restart test exists to rule out: a process that keeps answering from
        // whatever it had last time. An unfitted model returns null, and the caller decides what to
        // do about an absent model rather than receiving an unfitted number that looks fitted.
        Assert.Null(HarVarianceModel.Unfitted().Predict(1d, 1d, 1d));
        Assert.Null(GarchVarianceModel.Unfitted().WarmedVariance([1d, 1d, 1d]));
        Assert.Null(GaussianHmmFilter.Unfitted().Filter([1d, 1d]));
        Assert.Null(GradientBoostedTreeModel.Unfitted().Predict([1d]));
    }

    [Fact]
    public void EveryFixtureIsReadIndependentlyRatherThanSharedBetweenLoads()
    {
        // Guards the test itself. If both "loads" returned the same cached contract, the comparison
        // below would be an identity check dressed up as a restart, and would pass however much
        // state leaked.
        FittedModelContract first = Read("har-realised-variance.json");
        FittedModelContract second = Read("har-realised-variance.json");

        Assert.NotSame(first, second);
        Assert.Equal(first.ArtifactHash, second.ArtifactHash);
    }

    /// <summary>
    /// Loads, forecasts, discards everything, loads again from the same file, forecasts again --
    /// and requires both to equal what the fitting library recorded.
    /// </summary>
    private static void AssertRestartIsIdentical(
        string fixture,
        Func<FittedModelContract, IReadOnlyList<IReadOnlyList<double>>, IReadOnlyList<double>> forecast)
    {
        FittedModelContract before = Read(fixture);
        ModelParityCheck recorded = before.ParityChecks[0];
        IReadOnlyList<double> first = forecast(before, recorded.Inputs);

        // Nothing survives here but the file on disk, which is the whole point.
        FittedModelContract after = Read(fixture);
        IReadOnlyList<double> second = forecast(after, recorded.Inputs);

        Assert.Equal(first.Count, second.Count);
        for (int index = 0; index < first.Count; index++)
        {
            // Bit-identical across the restart. Anything less means state survived that should not
            // have, and the model is no longer a function of the artifact and its inputs.
            Assert.True(
                BitConverter.DoubleToInt64Bits(first[index])
                    == BitConverter.DoubleToInt64Bits(second[index]),
                $"element {index} moved across a restart: {first[index]} then {second[index]}");

            // And it is the answer Python got, not merely a stable answer of our own.
            Assert.True(
                before.Tolerance.Accepts(first[index], recorded.Expected[index]),
                $"element {index} is {first[index]}, the library recorded {recorded.Expected[index]}");
        }
    }

    private static RuntimeFeatureContract Runtime(FittedModelContract artifact) => new(
        artifact.FeatureSchemaHash,
        artifact.FeatureSemantics!.Units,
        artifact.FeatureSemantics.MissingPolicy,
        artifact.FeatureSemantics.BarDurationMinutes);

    private static FittedModelContract Read(string name) =>
        FittedModelArtifactReader.ReadFile(Path.Combine(FixtureRoot, name));

    private static readonly string FixtureRoot = LocateFixtures();

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
