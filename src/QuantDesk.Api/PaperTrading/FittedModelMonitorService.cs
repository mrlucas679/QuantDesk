using System.Text.Json;
using QuantDesk.Domain.Contracts;
using QuantDesk.Runtime.Experts;
using QuantDesk.Runtime.Research;
using QuantDesk.Runtime.Time;

namespace QuantDesk.Api.PaperTrading;

/// <summary>
/// Loads the models the research plane fits, and refuses the ones this runtime cannot reproduce.
///
/// The gap this closes
/// -------------------
/// The model bridge had two ends and no middle. Python could seal a fitted artifact and C# could
/// refuse or accept one, and nothing carried a file between them -- so a two-language inference
/// path verified against real libraries, real fixtures and real parity vectors could not affect a
/// single decision. <c>FittedModelArtifactReader</c> had zero production references until this
/// service.
///
/// What it checks, and why each check can fail
/// -------------------------------------------
/// The runtime states its own feature contract -- the schema hash it derives from what it computes,
/// the units, the missing policy, the bar. The artifact is measured against that rather than
/// against itself, so a model fitted on different features, or the same features in different
/// units, is refused rather than loaded. Then the loader replays the artifact's parity cases
/// through this runtime's own inference code, and refuses unless every one reproduces.
///
/// Refusal keeps what already worked
/// ---------------------------------
/// A model that fails any of that leaves the previously adopted one in place. Dropping to unfitted
/// on a bad file would let a half-written research cycle silently change what the trading path
/// forecasts, which is a worse outcome than declining to adopt.
/// </summary>
public sealed class FittedModelMonitorService(
    FittedModelStore store,
    IRuntimeClock clock,
    ILogger<FittedModelMonitorService> logger) : BackgroundService
{
    /// <summary>Where the research plane writes, mounted read-only here.</summary>
    private readonly string _root = Path.Combine(
        Environment.GetEnvironmentVariable("QUANTDESK_RESEARCH_ARTIFACT_ROOT")
        ?? "/app/research-artifacts",
        "fitted-models");

    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(30);

    private const string PointerName = "current-fitted-models.json";

    private string? _lastPointerFingerprint;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Probe();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A model that cannot be read must not stop the host. The store keeps whatever it
                // last adopted, which for a fresh deployment is nothing, and the expert keeps
                // reporting itself unfitted.
                logger.LogWarning(exception, "Fitted model probe failed; keeping the current models.");
            }

            await Task.Delay(ProbeInterval, stoppingToken);
        }
    }

    /// <summary>One pass: read the pointer, load what it names, adopt what verifies.</summary>
    internal void Probe()
    {
        string pointerPath = Path.Combine(_root, PointerName);
        if (!File.Exists(pointerPath))
        {
            store.Record([Absent("har"), Absent("garch")]);
            return;
        }

        string pointerJson = File.ReadAllText(pointerPath);

        // The pointer is written last and atomically, so its content changing is the signal that a
        // complete set arrived. Reloading unchanged artifacts every thirty seconds would re-run
        // parity on every one for no reason.
        if (string.Equals(pointerJson, _lastPointerFingerprint, StringComparison.Ordinal)) return;

        using JsonDocument pointer = JsonDocument.Parse(pointerJson);
        if (!pointer.RootElement.TryGetProperty("models", out JsonElement models)
            || models.ValueKind is not JsonValueKind.Object)
        {
            logger.LogWarning("Fitted model pointer names no models.");
            return;
        }

        var status = new List<FittedModelStatus>();
        status.Add(LoadHar(models));
        status.Add(LoadGarch(models));
        store.Record(status);

        _lastPointerFingerprint = pointerJson;

        foreach (FittedModelStatus entry in status)
        {
            if (entry.Loaded)
            {
                logger.LogInformation(
                    "Adopted fitted {Family} model {ArtifactId} from {Library} at {Time:O}.",
                    entry.Family, entry.ArtifactId, entry.ProducerLibrary, clock.UtcNow);
            }
            else
            {
                logger.LogInformation(
                    "Fitted {Family} model not adopted: {Rejection}.", entry.Family, entry.Rejection);
            }
        }
    }

    private FittedModelStatus LoadHar(JsonElement models)
    {
        if (!TryRead(models, "har", out FittedModelContract? artifact, out string failure))
            return new FittedModelStatus("har", false, failure, null, null);

        if (!HarVarianceModel.TryLoad(
                artifact!, RealizedVolatilityExpert.FeatureContract,
                out HarVarianceModel model, out FittedModelRejection rejection))
        {
            return new FittedModelStatus(
                "har", false, rejection.ToString(), artifact!.ArtifactId, artifact.ProducerLibrary);
        }

        store.Adopt(model);
        return new FittedModelStatus(
            "har", true, nameof(FittedModelRejection.None),
            artifact!.ArtifactId, $"{artifact.ProducerLibrary} {artifact.ProducerLibraryVersion}");
    }

    private FittedModelStatus LoadGarch(JsonElement models)
    {
        if (!TryRead(models, "garch", out FittedModelContract? artifact, out string failure))
            return new FittedModelStatus("garch", false, failure, null, null);

        if (!GarchVarianceModel.TryLoad(
                artifact!, GarchFeatureContract(artifact!),
                out GarchVarianceModel model, out FittedModelRejection rejection))
        {
            return new FittedModelStatus(
                "garch", false, rejection.ToString(), artifact!.ArtifactId, artifact.ProducerLibrary);
        }

        store.Adopt(model);
        return new FittedModelStatus(
            "garch", true, nameof(FittedModelRejection.None),
            artifact!.ArtifactId, $"{artifact.ProducerLibrary} {artifact.ProducerLibraryVersion}");
    }

    /// <summary>
    /// What this runtime feeds a GARCH model.
    ///
    /// The schema hash is derived, like HAR's. The units come from the artifact's own declaration
    /// only because the runtime does not yet compute this feature at all -- nothing feeds a
    /// squared residual series today, so there is no second opinion to compare against. That makes
    /// the units check vacuous for GARCH, and saying so here is better than letting it read as
    /// verified. It becomes real the moment a caller starts supplying the series.
    /// </summary>
    private static RuntimeFeatureContract GarchFeatureContract(FittedModelContract artifact) => new(
        FeatureSchemaDigest.Compute(
            schemaVersion: "garch11-zero-mean-v1",
            featureNames: GarchVarianceModel.FeatureNames,
            dtypes: GarchVarianceModel.FeatureNames.ToDictionary(name => name, _ => "float64"),
            lookbackPeriods: GarchVarianceModel.SchemaLookbackBars,
            sourceRequirements: ["alpaca_ohlcv"]),
        artifact.FeatureSemantics?.Units
            ?? new Dictionary<string, string>(StringComparer.Ordinal),
        artifact.FeatureSemantics?.MissingPolicy ?? string.Empty,
        artifact.FeatureSemantics?.BarDurationMinutes ?? 0);

    private bool TryRead(
        JsonElement models, string family, out FittedModelContract? artifact, out string failure)
    {
        artifact = null;
        failure = string.Empty;

        if (!models.TryGetProperty(family, out JsonElement named)
            || named.ValueKind is not JsonValueKind.String)
        {
            failure = "NOT_PUBLISHED";
            return false;
        }

        string name = named.GetString() ?? string.Empty;

        // A name the research plane chose, used as a path. Anything with a separator in it is not a
        // file this service is entitled to open.
        if (string.IsNullOrWhiteSpace(name)
            || name.IndexOfAny(['/', '\\']) >= 0
            || name.Contains("..", StringComparison.Ordinal))
        {
            failure = "UNSAFE_ARTIFACT_NAME";
            return false;
        }

        string path = Path.Combine(_root, name);
        if (!File.Exists(path))
        {
            failure = "ARTIFACT_MISSING";
            return false;
        }

        try
        {
            artifact = FittedModelArtifactReader.ReadFile(path);
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException)
        {
            failure = "UNREADABLE_ARTIFACT";
            logger.LogWarning(exception, "Fitted {Family} artifact {Name} could not be read.", family, name);
            return false;
        }
    }

    private static FittedModelStatus Absent(string family) =>
        new(family, false, "NO_POINTER", null, null);
}
