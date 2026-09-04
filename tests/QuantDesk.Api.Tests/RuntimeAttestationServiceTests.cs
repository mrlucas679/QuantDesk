using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using QuantDesk.Api.PaperTrading;
using QuantDesk.Domain.Market;
using QuantDesk.Runtime.Ingestion;
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
    public void QueueBoundsAreReadFromWhetherTheBoundWasEverReached()
    {
        // Every channel here is bounded by construction, so declaring a capacity answers nothing.
        // The property that matters is whether market data this process had already received was
        // dropped, and that is a counter rather than a design statement.
        var channel = new BoundedEventChannel<NormalizedMarketEvent>(capacity: 1);

        Assert.True(Service(channel: channel).Compose().BoundedQueues);

        channel.TryPublish(default, 1L);
        channel.TryPublish(default, 2L);

        RuntimeAttestationDocument overflowed = Service(channel: channel).Compose();
        Assert.False(overflowed.BoundedQueues);
        Assert.Equal(1L, overflowed.MarketDataEventsRejected);
    }

    [Fact]
    public void AReconnectLoopIsReportedEvenThoughEveryConnectionSucceeded()
    {
        // The failure this catches keeps every health check green: each connection works, data
        // flows in bursts, readiness flickers back to healthy between drops. What it destroys is
        // the book, because this venue publishes no sequence number and every reconnect loses an
        // unknown number of updates.
        var clock = new LiveRuntimeClock();
        var connections = new StreamConnectionTracker(clock);

        for (int cycle = 0; cycle <= StreamConnectionTracker.MaximumReconnectsInWindow; cycle++)
        {
            connections.Record("crypto", connected: true, clock.UtcNow);
            connections.Record("crypto", connected: false, clock.UtcNow);
        }

        RuntimeAttestationDocument document = Service(connections: connections).Compose();

        Assert.False(document.NoReconnectLeak);
        Assert.True(document.StreamReconnects > StreamConnectionTracker.MaximumReconnectsInWindow);
    }

    [Fact]
    public void AQuietRuntimeIsNotReportedAsLeaking()
    {
        RuntimeAttestationDocument document = Service().Compose();

        // Nothing has connected, so nothing has flapped. This is measured rather than unmeasured --
        // the queue and reconnect properties no longer appear in notMeasured at all.
        Assert.True(document.NoReconnectLeak);
        Assert.True(document.BoundedQueues);
        Assert.DoesNotContain("boundedQueues", document.NotMeasured);
        Assert.DoesNotContain("noReconnectLeak", document.NotMeasured);
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
    public void BrokerBehaviourIsAttestedOnlyFromAFullyCoveredCampaign()
    {
        // Read through coverage, never the pass count. A campaign where two thirds of the cases had
        // no driver would report every case it ran as passing, and reading that number would let a
        // subset stand in for a claim about the whole execution plane.
        RuntimeAttestationDocument document = Service().Compose();

        // No report file in a test environment, so nothing may be attested.
        Assert.False(document.AmbiguousSubmitResolvesUnknown);
        Assert.False(document.PendingOrderInvalidation);
        Assert.Contains("ambiguousSubmitResolvesUnknown", document.NotMeasured);
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
        LatencyRecorder? latency = null,
        SessionReplayState? replay = null,
        BoundedEventChannel<NormalizedMarketEvent>? channel = null,
        StreamConnectionTracker? connections = null)
    {
        var clock = new LiveRuntimeClock();
        return new RuntimeAttestationService(
            new FullSystemReadinessState(clock),
            new SpotExecutionRecoveryService(
                null!, NullLogger<SpotExecutionRecoveryService>.Instance, clock),
            replay ?? new SessionReplayState(),
            latency ?? new LatencyRecorder(),
            channel ?? new BoundedEventChannel<NormalizedMarketEvent>(capacity: 64),
            connections ?? new StreamConnectionTracker(clock),
            clock,
            NullLogger<RuntimeAttestationService>.Instance);
    }
}
