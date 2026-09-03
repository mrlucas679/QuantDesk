using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using QuantDesk.Api.PaperTrading;
using QuantDesk.Runtime.Experts;
using QuantDesk.Runtime.Indicators;
using QuantDesk.Runtime.Research;
using QuantDesk.Runtime.Time;

namespace QuantDesk.Api.Tests;

/// <summary>
/// The loop from a fitted artifact on disk to a forecast that changes because of it.
///
/// What this is really testing
/// ---------------------------
/// Every piece of the model bridge was verified in isolation and none of it was connected: the
/// reader had zero production references, and the volatility expert was registered, reachable and
/// permanently unfitted. Each test below fails if any link in that chain comes apart, which is a
/// different guarantee from every test that came before it.
///
/// The fixtures are the artifacts Python actually writes, at the windows this runtime actually
/// computes. That second part was not true until now -- the committed HAR fixture was fitted at the
/// daily 1 / 5 / 22 convention while the expert serves 12 / 60 / 288, so it described a model this
/// runtime would compute different features for.
/// </summary>
public sealed class FittedModelMonitorServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"quantdesk-models-{Guid.NewGuid():N}");

    private readonly string? _previousRoot =
        Environment.GetEnvironmentVariable("QUANTDESK_RESEARCH_ARTIFACT_ROOT");

    public FittedModelMonitorServiceTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "fitted-models"));
        Environment.SetEnvironmentVariable("QUANTDESK_RESEARCH_ARTIFACT_ROOT", _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("QUANTDESK_RESEARCH_ARTIFACT_ROOT", _previousRoot);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void APublishedHarArtifactIsAdoptedAndDrivesTheVolatilityExpert()
    {
        // The end of the chain the whole bridge exists for.
        Publish(har: "har-realised-variance.json");
        var store = new FittedModelStore();

        Monitor(store).Probe();

        FittedModelStatus har = store.Status.Single(entry => entry.Family == "har");
        Assert.True(har.Loaded, har.Rejection);
        Assert.Contains("quantdesk_research", har.ProducerLibrary);
        Assert.True(new RealizedVolatilityExpert(store).IsFitted);
    }

    [Fact]
    public void TheFittedForecastDiffersFromTheConventionalOne()
    {
        // Adoption that changed no number would be adoption in name only.
        Publish(har: "har-realised-variance.json");
        var store = new FittedModelStore();
        Monitor(store).Probe();

        IndicatorSet indicators = Bars();
        double? unfitted = Forecast(new RealizedVolatilityExpert(), indicators);
        double? fitted = Forecast(new RealizedVolatilityExpert(store), indicators);

        Assert.NotNull(unfitted);
        Assert.NotNull(fitted);
        Assert.NotEqual(unfitted!.Value, fitted!.Value, precision: 12);
    }

    [Fact]
    public void TheRuntimeDerivesTheSchemaItExpectsRatherThanTakingTheArtifactsWordForIt()
    {
        // If the runtime read the hash out of the artifact, this would pass for a model fitted on
        // anything at all. The contract is built from what the expert computes.
        Publish(har: "har-realised-variance.json");

        string committed = SchemaHashOf("har-realised-variance.json");
        Assert.Equal(committed, RealizedVolatilityExpert.FeatureContract.FeatureSchemaHash);
    }

    [Fact]
    public void AnArtifactFittedOnADifferentFeatureSetIsRefused()
    {
        // The refusal the schema hash exists for, exercised end to end rather than in a unit test
        // with a hand-made contract.
        Publish(har: "garch-conditional-variance.json");
        var store = new FittedModelStore();

        Monitor(store).Probe();

        FittedModelStatus har = store.Status.Single(entry => entry.Family == "har");
        Assert.False(har.Loaded);
        Assert.False(new RealizedVolatilityExpert(store).IsFitted);
    }

    [Fact]
    public void ARefusedArtifactLeavesThePreviouslyAdoptedOneInPlace()
    {
        // Dropping to unfitted on a bad file would let a half-written research cycle silently
        // change what the trading path forecasts. Declining to adopt is the conservative move.
        Publish(har: "har-realised-variance.json");
        var store = new FittedModelStore();
        FittedModelMonitorService monitor = Monitor(store);
        monitor.Probe();
        Assert.True(new RealizedVolatilityExpert(store).IsFitted);

        Publish(har: "garch-conditional-variance.json");
        monitor.Probe();

        Assert.True(new RealizedVolatilityExpert(store).IsFitted);
    }

    [Fact]
    public void AnArtifactNameThatIsAPathIsRefusedRatherThanOpened()
    {
        // The name comes from a file another process wrote. It is used as a path.
        WritePointer(new Dictionary<string, string> { ["har"] = "../../etc/passwd" });
        var store = new FittedModelStore();

        Monitor(store).Probe();

        Assert.Equal(
            "UNSAFE_ARTIFACT_NAME", store.Status.Single(entry => entry.Family == "har").Rejection);
    }

    [Fact]
    public void NoPointerMeansUnfittedRatherThanAnError()
    {
        // A fresh deployment has an empty volume, and that is an ordinary state rather than a fault.
        var store = new FittedModelStore();

        Monitor(store).Probe();

        Assert.All(store.Status, entry => Assert.Equal("NO_POINTER", entry.Rejection));
        Assert.False(new RealizedVolatilityExpert(store).IsFitted);
    }

    [Fact]
    public void AnUnreadableArtifactIsRefusedWithoutStoppingTheProbe()
    {
        File.WriteAllText(Path.Combine(_root, "fitted-models", "broken.json"), "{ not json");
        WritePointer(new Dictionary<string, string> { ["har"] = "broken.json" });
        var store = new FittedModelStore();

        Monitor(store).Probe();

        Assert.Equal(
            "UNREADABLE_ARTIFACT", store.Status.Single(entry => entry.Family == "har").Rejection);
    }

    // ------------------------------------------------------------------------------- fixtures

    private FittedModelMonitorService Monitor(FittedModelStore store) =>
        new(store, new LiveRuntimeClock(), NullLogger<FittedModelMonitorService>.Instance);

    private void Publish(string har)
    {
        File.Copy(FixturePath(har), Path.Combine(_root, "fitted-models", har), overwrite: true);
        WritePointer(new Dictionary<string, string> { ["har"] = har });
    }

    private void WritePointer(Dictionary<string, string> models)
    {
        File.WriteAllText(
            Path.Combine(_root, "fitted-models", "current-fitted-models.json"),
            JsonSerializer.Serialize(new
            {
                dataset_hash = Guid.NewGuid().ToString("N"),
                models,
            }));
    }

    private static double? Forecast(RealizedVolatilityExpert expert, IndicatorSet indicators) =>
        expert.Forecast(indicators, 0, 20, TimeSpan.FromMinutes(5), 1, 1, 1_000, 1)
            ?.ExpectedRealizedVariance;

    /// <summary>Bars with enough history for the long window, and a volatility pattern in them.</summary>
    private static IndicatorSet Bars()
    {
        const int count = RealizedVolatilityExpert.LongBars + 60;
        var closes = new decimal[count];
        double price = 30_000d;
        var random = new Random(11);

        for (int index = 0; index < count; index++)
        {
            // Wider moves in the recent window than the distant one, so the short and long
            // components disagree and a change of weights moves the answer. Identical components
            // would make any set of weights produce the same number, and the test would pass
            // whether the artifact had been adopted or not.
            double scale = index > count - 80 ? 0.004d : 0.0008d;
            price *= Math.Exp((random.NextDouble() - 0.5d) * scale);
            closes[index] = (decimal)price;
        }

        return IndicatorSet.Unwarmed(closes);
    }

    private static string SchemaHashOf(string name)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(FixturePath(name)));
        return document.RootElement.GetProperty("feature_schema_hash").GetString()!;
    }

    private static string FixturePath(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, ".git")))
            directory = directory.Parent;

        return Path.Combine(
            directory?.FullName ?? AppContext.BaseDirectory,
            "tests", "fixtures", "model-artifacts", name);
    }
}
