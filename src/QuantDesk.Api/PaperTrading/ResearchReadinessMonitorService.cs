using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuantDesk.Api.PaperTrading;

/// <summary>Mirrors research-plane readiness into the C# fail-closed readiness ledger.</summary>
public sealed class ResearchReadinessMonitorService(
    HttpClient httpClient,
    FullSystemReadinessState readiness,
    ResearchArtifactState artifacts,
    ILogger<ResearchReadinessMonitorService> logger) : BackgroundService
{
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(10);

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
            using HttpResponseMessage response = await httpClient.GetAsync("readiness", cancellationToken);
            ResearchReadinessResponse? research = JsonSerializer.Deserialize<ResearchReadinessResponse>(
                await response.Content.ReadAsStreamAsync(cancellationToken));
            ResearchArtifactSnapshot artifact = artifacts.Snapshot();
            readiness.RecordResearchPlane(
                research is { Ready: true, FeaturesReady: true } && artifact.Ready,
                research is { Ready: true, ExpertsReady: true, ValidatedModelCount: > 0 } && artifact.Ready);
        }
        catch (Exception exception) when (HostedServiceFaults.IsFault(exception, cancellationToken))
        {
            readiness.RecordResearchPlane(false, false);
            logger.LogWarning(exception, "QuantDesk research readiness probe failed closed.");
        }
    }

    private sealed record ResearchReadinessResponse(
        bool Ready,
        [property: JsonPropertyName("validated_model_count")] int ValidatedModelCount,
        [property: JsonPropertyName("features_ready")] bool FeaturesReady,
        [property: JsonPropertyName("experts_ready")] bool ExpertsReady);
}
