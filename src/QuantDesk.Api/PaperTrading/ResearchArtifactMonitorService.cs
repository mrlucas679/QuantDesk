using System.Text.Json;
using QuantDesk.Domain.Contracts;
using QuantDesk.Runtime.Research;

namespace QuantDesk.Api.PaperTrading;

/// <summary>Holds the latest verified research publication available to execution.</summary>
public sealed class ResearchArtifactState
{
    private readonly object _sync = new();
    private ResearchArtifactSnapshot _snapshot = new(false, "no_verified_research_artifact", null, null, null, null);

    public ResearchArtifactSnapshot Snapshot()
    {
        lock (_sync) return _snapshot;
    }

    public void RecordValid(ModelArtifactContract artifact, ForecastSnapshotContract forecast)
    {
        lock (_sync) _snapshot = new(
            true, "verified", artifact.ArtifactId, artifact.StrategyFamily, forecast.AsOfTime, forecast);
    }

    public void RecordInvalid(string reason)
    {
        lock (_sync) _snapshot = new(false, reason, null, null, null, null);
    }
}

public sealed record ResearchArtifactSnapshot(
    bool Ready,
    string Reason,
    string? ArtifactId,
    string? StrategyFamily,
    DateTimeOffset? ForecastAsOfTime,
    ForecastSnapshotContract? Forecast);

/// <summary>
/// Reads an atomically published research pointer from the read-only artifact volume and
/// fails closed unless the schema, artifact, and fresh forecast have exact matching hashes.
/// </summary>
public sealed class ResearchArtifactMonitorService(
    ResearchArtifactState state,
    ILogger<ResearchArtifactMonitorService> logger) : BackgroundService
{
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(10);
    private readonly string _artifactRoot = Environment.GetEnvironmentVariable("QUANTDESK_RESEARCH_ARTIFACT_ROOT")
        ?? "/app/research-artifacts";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await ProbeAsync(stoppingToken);
            await Task.Delay(ProbeInterval, stoppingToken);
        }
    }

    private async Task ProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            string pointerJson = await File.ReadAllTextAsync(Path.Combine(_artifactRoot, "current-contracts.json"), cancellationToken);
            ArtifactPointer? pointer = JsonSerializer.Deserialize<ArtifactPointer>(pointerJson);
            if (pointer is null) throw new InvalidDataException("Artifact pointer is empty.");

            string schemaJson = await ReadNamedFileAsync(pointer.FeatureSchema, cancellationToken);
            string artifactJson = await ReadNamedFileAsync(pointer.ModelArtifact, cancellationToken);
            string forecastJson = await ReadNamedFileAsync(pointer.Forecast, cancellationToken);
            FeatureSchemaContract schema = PythonResearchContractReader.ReadFeatureSchema(schemaJson);
            ModelArtifactContract artifact = PythonResearchContractReader.ReadModelArtifact(artifactJson);
            ForecastSnapshotContract forecast = PythonResearchContractReader.ReadForecast(forecastJson);
            PythonResearchContractReader.ValidateForecast(artifact, schema, forecast);
            if (!string.Equals(forecast.Status, "valid", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Forecast publication is not valid.");
            if (DateTimeOffset.UtcNow - forecast.AsOfTime > TimeSpan.FromMinutes(forecast.HorizonMinutes))
                throw new InvalidDataException("Forecast publication is stale.");
            state.RecordValid(artifact, forecast);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            state.RecordInvalid("artifact_unavailable_or_invalid");
            logger.LogDebug(exception, "Research artifact probe failed closed.");
        }
    }

    private async Task<string> ReadNamedFileAsync(string name, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name) || Path.GetFileName(name) != name)
            throw new InvalidDataException("Artifact pointer contains an invalid file name.");
        return await File.ReadAllTextAsync(Path.Combine(_artifactRoot, name), cancellationToken);
    }

    private sealed record ArtifactPointer(string FeatureSchema, string ModelArtifact, string Forecast);
}
