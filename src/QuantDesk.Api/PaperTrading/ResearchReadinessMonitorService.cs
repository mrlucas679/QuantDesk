using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuantDesk.Api.PaperTrading;

/// <summary>
/// Mirrors research-plane readiness into the C# fail-closed readiness ledger.
///
/// Not ready is an answer, not a fault
/// -----------------------------------
/// The research plane returns 503 with a well-formed body while it is still collecting evidence —
/// "no_validated_models", 651 of 8,640 unseen bars — which is the correct thing for it to say and
/// the expected state for most of a campaign. So the body is read whatever the status code, and a
/// negative answer is recorded quietly. Only an unreachable or unintelligible plane is a fault.
///
/// Conflating the two was live on 2026-09-02: every probe logged a warning with a socket-level
/// stack trace roughly every ten seconds, for a condition that was neither an error nor actionable.
/// That is how an operator learns to skip the line that matters when the plane really does break.
///
/// The timeout has to outlast the answer
/// -------------------------------------
/// The probe's client timeout was five seconds against a <c>/readiness</c> endpoint measured at
/// 4.1, 5.0 and 8.5 seconds. So the ledger was being written from a timeout rather than from the
/// plane's reply, and it would have gone on being written from a timeout after research became
/// ready — the gate would have kept refusing entries with the plane sitting green behind it. A
/// readiness mirror does not need fine granularity, so the budget is generous and the interval
/// longer than the budget, which also stops slow probes overlapping each other.
/// </summary>
public sealed class ResearchReadinessMonitorService(
    HttpClient httpClient,
    FullSystemReadinessState readiness,
    ResearchArtifactState artifacts,
    ILogger<ResearchReadinessMonitorService> logger) : BackgroundService
{
    /// <summary>
    /// How long the plane is given to answer.
    ///
    /// Public so the client registration cannot drift away from it. A timeout shorter than the
    /// endpoint's own latency does not degrade the signal, it replaces it.
    /// </summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Longer than <see cref="ProbeTimeout"/>, so a slow probe cannot overlap the next.</summary>
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await ProbeAsync(stoppingToken);
            await Task.Delay(ProbeInterval, stoppingToken);
        }
    }

    internal async Task ProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await httpClient.GetAsync("readiness", cancellationToken);

            // The status code is not consulted. A 503 carrying a readiness body is the plane saying
            // "not ready" in the way it is designed to, and refusing to read it would discard the
            // reason along with the answer.
            ResearchReadinessResponse? research = JsonSerializer.Deserialize<ResearchReadinessResponse>(
                await response.Content.ReadAsStreamAsync(cancellationToken));

            if (research is null)
            {
                // Reached, but unintelligible. That is a fault: something is answering on the
                // research port that is not the research plane, and the ledger must not treat the
                // resulting false as a considered negative.
                readiness.RecordResearchPlane(false, false);
                logger.LogWarning(
                    "QuantDesk research readiness returned {StatusCode} with no readable body.",
                    (int)response.StatusCode);
                return;
            }

            ResearchArtifactSnapshot artifact = artifacts.Snapshot();
            bool features = research is { Ready: true, FeaturesReady: true } && artifact.Ready;
            bool experts =
                research is { Ready: true, ExpertsReady: true, ValidatedModelCount: > 0 } && artifact.Ready;
            readiness.RecordResearchPlane(features, experts);

            if (!features || !experts)
            {
                logger.LogDebug(
                    "QuantDesk research plane is not ready: {Reason} ({ValidatedModels} validated models).",
                    research.Reason ?? "unspecified",
                    research.ValidatedModelCount);
            }
        }
        catch (Exception exception) when (HostedServiceFaults.IsFault(exception, cancellationToken))
        {
            readiness.RecordResearchPlane(false, false);
            logger.LogWarning(exception, "QuantDesk research readiness probe could not reach the plane.");
        }
    }

    private sealed record ResearchReadinessResponse(
        bool Ready,
        [property: JsonPropertyName("validated_model_count")] int ValidatedModelCount,
        [property: JsonPropertyName("features_ready")] bool FeaturesReady,
        [property: JsonPropertyName("experts_ready")] bool ExpertsReady)
    {
        /// <summary>Why the plane is not ready, carried into the log so the state is explicable.</summary>
        [property: JsonPropertyName("reason")]
        public string? Reason { get; init; }
    }
}
