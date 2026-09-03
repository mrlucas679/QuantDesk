using System.Text.Json;
using QuantDesk.Domain.Market;
using QuantDesk.Runtime.Execution;
using QuantDesk.Runtime.Ingestion;
using QuantDesk.Runtime.Reliability;
using QuantDesk.Runtime.Telemetry;
using QuantDesk.Runtime.Time;

namespace QuantDesk.Api.PaperTrading;

/// <summary>
/// Writes what only the execution plane can say about itself, for the research plane's R11 and R12.
///
/// Why this crosses the boundary at all
/// ------------------------------------
/// Promotion requires evidence for ten gates. Eight are properties of a candidate and its backtest,
/// and research measures them. Two are properties of the running system -- restart-safe client order
/// ids, reconciliation, bounded queues, measured latency, a verified paper endpoint -- and research
/// cannot observe any of it. Asserting them from the Python side would be the fabrication the whole
/// gate evaluator exists to prevent, so they are answered here, by the process that knows.
///
/// What is attested and what is merely hoped
/// -----------------------------------------
/// Every flag below is read from something. Where nothing measures a property yet, the flag is
/// false and its name appears in <c>notMeasured</c>: the gate then fails for a stated reason rather
/// than passing on an assumption. That is the whole point -- an attestation that reported what the
/// design intends rather than what the runtime observes would be a more elaborate version of the
/// tautology the fault campaign used to be.
///
/// The fault campaign is the evidence for the broker-behaviour clauses, and it is deliberately read
/// through <see cref="FaultCampaignReport.FullyCovered"/> rather than through its pass count. Seven
/// of its twenty-one cases have drivers; the ones that would demonstrate ambiguous-submit and
/// pending-order handling are not among them. A subset cannot support a claim about the whole.
/// </summary>
public sealed class RuntimeAttestationService(
    FullSystemReadinessState readiness,
    SpotExecutionRecoveryService recovery,
    SessionReplayState replay,
    LatencyRecorder latency,
    BoundedEventChannel<NormalizedMarketEvent> marketDataChannel,
    StreamConnectionTracker connections,
    IRuntimeClock clock,
    ILogger<RuntimeAttestationService> logger) : BackgroundService
{
    /// <summary>
    /// Where the research plane reads it. Both containers mount the same volume: the runtime has it
    /// at /app/research-data, the research worker at /app/data.
    /// </summary>
    private static readonly string DefaultPath =
        Path.Combine("/app/research-data", "runtime-attestation.json");

    /// <summary>
    /// Rewritten often enough that the research plane's staleness bound is never the binding
    /// constraint, and rarely enough that it is not a write loop.
    /// </summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    /// <summary>How recently the recovery loop must have completed a clean cycle.</summary>
    private static readonly TimeSpan RecoveryFreshness = TimeSpan.FromMinutes(2);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Write();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A runtime that cannot write its attestation must not stop trading over it. The
                // research plane already fails both gates closed when the file is missing or stale,
                // so the consequence of this failing is that nothing is promoted -- which is the
                // safe direction.
                logger.LogWarning(exception, "Runtime attestation could not be written.");
            }

            await Task.Delay(Interval, stoppingToken);
        }
    }

    internal void Write(string? path = null)
    {
        RuntimeAttestationDocument document = Compose();
        string target = path ?? Environment.GetEnvironmentVariable("QUANTDESK_ATTESTATION_PATH")
            ?? DefaultPath;

        string? directory = Path.GetDirectoryName(Path.GetFullPath(target));
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

        // Written whole then moved, so a reader never sees half a document and decides on it.
        string temporary = target + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(document, JsonOptions));
        File.Move(temporary, target, overwrite: true);
    }

    internal RuntimeAttestationDocument Compose()
    {
        FullSystemReadinessSnapshot snapshot = readiness.Snapshot();
        FaultCampaignReport? campaign = LoadCampaign();
        bool campaignProvesBrokerBehaviour = campaign?.FullyCovered ?? false;

        var notMeasured = new List<string>();
        if (!campaignProvesBrokerBehaviour)
        {
            notMeasured.Add("ambiguousSubmitResolvesUnknown");
            notMeasured.Add("pendingOrderInvalidation");
        }


        return new RuntimeAttestationDocument(
            AttestedAt: clock.UtcNow,
            DeterministicClientOrderIds: ClientOrderIdsAreReproducible(),
            AmbiguousSubmitResolvesUnknown: campaignProvesBrokerBehaviour,
            ReservationBeforeSubmit: snapshot.ReservationReady,
            ReconciliationHealthy: ReconciliationIsHealthy(),
            PendingOrderInvalidation: campaignProvesBrokerBehaviour,
            // Every channel here is bounded by construction, so the capacity is not the question --
            // whether the bound was ever reached is. A rejection means market data this process had
            // already received was dropped on the floor.
            BoundedQueues: marketDataChannel.WithinBounds,

            // Not "is the stream up" -- a socket that redials every few seconds keeps every health
            // check green while destroying the book, because this venue publishes no sequence
            // number and each reconnect loses an unknown number of updates.
            NoReconnectLeak: connections.NoReconnectLeak(),
            PaperEndpointVerified: snapshot.PaperEndpointVerified,
            DecisionPathP99Milliseconds: PercentileOf(LatencyStage.Decision),
            DataAgeP99Milliseconds: PercentileOf(LatencyStage.MarketDataFetch),
            ReplayTraceHash: replay.Snapshot().TraceHash,
            MarketDataEventsRejected: marketDataChannel.Rejected,
            MarketDataChannelHighWater: marketDataChannel.HighWater,
            MarketDataChannelCapacity: marketDataChannel.Capacity,
            StreamReconnects: connections.Summarise()
                .Sum(summary => summary.ReconnectsInWindow),
            FaultCampaignExercised: campaign?.Exercised ?? 0,
            FaultCampaignTotal: campaign?.Total ?? FaultCampaign.Cases.Count,
            NotMeasured: notMeasured);
    }

    /// <summary>
    /// Derives the same client order id twice and compares.
    ///
    /// A real check rather than a restatement of the design: the whole recovery story rests on the
    /// id being reproducible from the opportunity alone, and a clock or a random value smuggled into
    /// that derivation would break recovery silently. This catches exactly that.
    /// </summary>
    private static bool ClientOrderIdsAreReproducible()
    {
        const string Lane = "auto";
        const string Identity = "attestation-probe-btcusd-2026-09-03T00:00:00Z";

        string first = DeterministicClientOrderId.Create(Lane, Identity, "entry");
        string second = DeterministicClientOrderId.Create(Lane, Identity, "entry");
        string exit = DeterministicClientOrderId.Create(Lane, Identity, "exit");

        // Equal for the same leg, and different across legs -- an id that collapsed both legs onto
        // one value would be reproducible and useless.
        return string.Equals(first, second, StringComparison.Ordinal)
            && !string.Equals(first, exit, StringComparison.Ordinal);
    }

    /// <summary>The recovery loop has completed a cycle recently, and without error.</summary>
    private bool ReconciliationIsHealthy() =>
        recovery.LastError is null
        && recovery.LastCycleAt is { } last
        && clock.UtcNow - last <= RecoveryFreshness;

    private double? PercentileOf(LatencyStage stage)
    {
        LatencySummary? summary = latency.Summarise()
            .Cast<LatencySummary?>()
            .FirstOrDefault(item => item!.Value.Stage == stage);

        // No observations means nothing has been measured, which is not the same as a fast path.
        return summary is { Count: > 0 } measured ? measured.P99 : null;
    }

    private static FaultCampaignReport? LoadCampaign()
    {
        string path = Environment.GetEnvironmentVariable("QUANTDESK_FAULT_CAMPAIGN_PATH")
            ?? Path.Combine(AppContext.BaseDirectory, "fault-campaign.json");

        try
        {
            return FaultCampaign.Load(path);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// The attested facts, in the shape the research plane reads.
/// </summary>
/// <param name="NotMeasured">
/// Flags reported false because nothing observes them yet, as opposed to false because the property
/// failed. Both fail their gate; only this says which needs building rather than fixing.
/// </param>
public sealed record RuntimeAttestationDocument(
    DateTimeOffset AttestedAt,
    bool DeterministicClientOrderIds,
    bool AmbiguousSubmitResolvesUnknown,
    bool ReservationBeforeSubmit,
    bool ReconciliationHealthy,
    bool PendingOrderInvalidation,
    bool BoundedQueues,
    bool NoReconnectLeak,
    bool PaperEndpointVerified,
    double? DecisionPathP99Milliseconds,
    double? DataAgeP99Milliseconds,
    string? ReplayTraceHash,
    long MarketDataEventsRejected,
    int MarketDataChannelHighWater,
    int MarketDataChannelCapacity,
    int StreamReconnects,
    int FaultCampaignExercised,
    int FaultCampaignTotal,
    IReadOnlyList<string> NotMeasured);
