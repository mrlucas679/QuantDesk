using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using QuantDesk.Api.PaperTrading;
using QuantDesk.Runtime.Telemetry;
using QuantDesk.Runtime.Time;

namespace QuantDesk.Api.Tests;

/// <summary>
/// What the execution plane tells the research plane about itself.
///
/// Gates R11 and R12 are properties of this process, and research cannot observe them. The risk in
/// answering them from here is that the answers become a restatement of the design rather than an
/// observation -- which is what the fault campaign was for its entire existence, reporting 21 of 21
/// from a table of its own expectations.
///
/// So these pin the opposite: an unmeasured property is reported false and named, a property that is
/// genuinely checked is checked, and nothing is asserted because the architecture intends it.
/// </summary>
public sealed class RuntimeAttestationServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"quantdesk-attest-{Guid.NewGuid():N}");

    public RuntimeAttestationServiceTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void PropertiesNothingMeasuresAreReportedFalseAndNamed()
    {
        // The whole point. Bounded queues and reconnect leaks are real R12 requirements that
        // nothing in this runtime counts yet, and an attestation claiming them would make the gate
        // pass on the strength of an intention.
        RuntimeAttestationDocument document = Service().Compose();

        Assert.False(document.BoundedQueues);
        Assert.False(document.NoReconnectLeak);
        Assert.Contains("boundedQueues", document.NotMeasured);
        Assert.Contains("noReconnectLeak", document.NotMeasured);
    }

    [Fact]
    public void ClientOrderIdReproducibilityIsCheckedRatherThanAsserted()
    {
        // Recovery rests entirely on the id being derivable from the opportunity alone. This is a
        // real derivation run twice, so a clock or random value smuggled into it would show up here.
        RuntimeAttestationDocument document = Service().Compose();

        Assert.True(document.DeterministicClientOrderIds);
    }

    [Fact]
    public void APartiallyCoveredFaultCampaignDoesNotAttestBrokerBehaviour()
    {
        // Seven of twenty-one cases have drivers, and the ones demonstrating ambiguous-submit and
        // pending-order handling are not among them. A subset cannot support a claim about the whole,
        // and reading the pass count instead of coverage is how that mistake gets made.
        RuntimeAttestationDocument document = Service().Compose();

        Assert.False(document.AmbiguousSubmitResolvesUnknown);
        Assert.False(document.PendingOrderInvalidation);
        Assert.True(document.FaultCampaignExercised < document.FaultCampaignTotal);
    }

    [Fact]
    public void ReconciliationIsUnhealthyUntilTheRecoveryLoopHasRun()
    {
        // A recovery service that has never completed a cycle has not reconciled anything, and
        // reporting it healthy because no error has been recorded yet would invert the meaning.
        RuntimeAttestationDocument document = Service().Compose();

        Assert.False(document.ReconciliationHealthy);
    }

    [Fact]
    public void AnUnmeasuredLatencyIsNullRatherThanZero()
    {
        // Zero would read as an instantaneous decision path and let R5 pass anything.
        RuntimeAttestationDocument document = Service().Compose();

        Assert.Null(document.DecisionPathP99Milliseconds);
    }

    [Fact]
    public void AMeasuredLatencyIsCarriedThrough()
    {
        var latency = new LatencyRecorder();
        latency.Record(LatencyStage.Decision, 42.0);
        latency.Record(LatencyStage.MarketDataFetch, 600.0);

        RuntimeAttestationDocument document = Service(latency: latency).Compose();

        Assert.NotNull(document.DecisionPathP99Milliseconds);
        Assert.NotNull(document.DataAgeP99Milliseconds);
    }

    [Fact]
    public void TheReplayTraceIsCarriedSoR12CanRequireAReproducedSession()
    {
        var replay = new SessionReplayState();
        replay.Update(new SessionReplaySnapshot(
            "replayed", "session-1.jsonl", 549, "9c6abc3a", null, DateTimeOffset.UtcNow));

        RuntimeAttestationDocument document = Service(replay: replay).Compose();

        Assert.Equal("9c6abc3a", document.ReplayTraceHash);
    }

    [Fact]
    public void TheDocumentIsWrittenWholeAndReadsBackAsTheResearchPlaneExpects()
    {
        string path = Path.Combine(_directory, "runtime-attestation.json");

        Service().Write(path);

        using JsonDocument written = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = written.RootElement;

        // camelCase, because the Python loader reads these exact keys.
        Assert.True(root.TryGetProperty("attestedAt", out _));
        Assert.True(root.TryGetProperty("deterministicClientOrderIds", out _));
        Assert.True(root.TryGetProperty("paperEndpointVerified", out _));
        Assert.True(root.TryGetProperty("decisionPathP99Milliseconds", out _));
        Assert.False(Directory.EnumerateFiles(_directory, "*.tmp").Any());
    }

    // ------------------------------------------------------------------------------- fixtures

    private RuntimeAttestationService Service(
        LatencyRecorder? latency = null, SessionReplayState? replay = null)
    {
        var clock = new LiveRuntimeClock();
        return new RuntimeAttestationService(
            new FullSystemReadinessState(clock),
            new SpotExecutionRecoveryService(
                null!, NullLogger<SpotExecutionRecoveryService>.Instance, clock),
            replay ?? new SessionReplayState(),
            latency ?? new LatencyRecorder(),
            clock,
            NullLogger<RuntimeAttestationService>.Instance);
    }
}
