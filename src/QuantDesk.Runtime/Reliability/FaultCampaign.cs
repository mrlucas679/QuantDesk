using System.Text.Json;
using System.Text.Json.Serialization;
using QuantDesk.Domain.Market;
using QuantDesk.Domain.Runtime;
using QuantDesk.Runtime.Persistence;
using QuantDesk.Runtime.State;

namespace QuantDesk.Runtime.Reliability;

public enum FaultDisposition
{
    RejectInput,
    Abstain,
    HaltLane,
    RecoverExisting,
    Reconcile,
    DegradeReadOnly
}

public sealed record FaultCampaignCase(
    string Id,
    string Category,
    string Injection,
    FaultDisposition ExpectedDisposition,
    bool BrokerMutationAllowed,
    string Recovery);

/// <param name="ObservedDisposition">
/// What production code actually did, or null when no driver exercises this case yet.
/// </param>
/// <param name="Exercised">Whether real code produced the observation.</param>
public sealed record FaultCampaignCaseResult(
    string Id,
    string Category,
    FaultDisposition ExpectedDisposition,
    FaultDisposition? ObservedDisposition,
    bool BrokerMutationAllowed,
    bool Exercised,
    bool Passed,
    string Recovery);

/// <param name="Exercised">How many cases a driver actually ran.</param>
/// <param name="Passed">How many exercised cases contained the fault as required.</param>
public sealed record FaultCampaignReport(
    string CampaignId,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    int Total,
    int Exercised,
    int Passed,
    IReadOnlyList<FaultCampaignCaseResult> Cases)
{
    /// <summary>No case that ran, failed. The bar for starting the system.</summary>
    public bool NoExercisedFailure => Passed == Exercised;

    /// <summary>
    /// Every case ran against production code and contained its fault. The bar for gate R11,
    /// which is a claim about the whole execution plane and cannot be made from a subset.
    /// </summary>
    public bool FullyCovered => Exercised == Total && Passed == Total;
}

/// <summary>
/// The release-critical 21-case failure campaign, run against production code.
///
/// What this used to be
/// --------------------
/// Every case was answered by a <c>switch</c> returning the same disposition the case declared it
/// expected. The comparison that followed was therefore between a constant and itself: the campaign
/// reported 21 of 21 passed, always, and touched no production code in doing so. It is a dependency
/// of API start-up, so what looked like a safety gate was a statement the build made about its own
/// table of expectations.
///
/// That is the same shape as an attribution whose residual is zero by construction: an assertion
/// arranged to be true rather than a measurement that could come back false.
///
/// What it is now
/// --------------
/// A case passes only when a driver drove real code and that code contained the fault as required.
/// A case with no driver reports <see cref="FaultCampaignCaseResult.Exercised"/> false and is
/// counted apart -- it can never be read as a pass, and coverage is reported rather than implied.
///
/// Start-up and promotion ask different questions of the result, deliberately. Start-up requires
/// that nothing which ran, failed; refusing to boot over a case nobody has written yet would take a
/// working paper system down for missing test coverage. Gate R11 requires full coverage, because
/// "the execution plane is safe under fault" is not a claim a third of a campaign can support.
/// </summary>
public static class FaultCampaign
{
    public const string CampaignId = "QUANTDESK-FAULT-21-V1";

    public static IReadOnlyList<FaultCampaignCase> Cases { get; } =
    [
        Case("DATA-01", "Data", "Crossed quote", FaultDisposition.RejectInput, "Wait for a valid book"),
        Case("DATA-02", "Data", "Missing quote", FaultDisposition.Abstain, "Await a complete quote"),
        Case("DATA-03", "Data", "Stale quote", FaultDisposition.Abstain, "Refresh market evidence"),
        Case("DATA-04", "Data", "Out-of-order event", FaultDisposition.RejectInput, "Retain the newer state version"),
        Case("DATA-05", "Data", "Sequence gap", FaultDisposition.HaltLane, "Resubscribe and rebuild market state"),
        Case("DATA-06", "Data", "Non-finite trade price", FaultDisposition.RejectInput, "Discard the malformed event"),
        Case("DATA-07", "Data", "Missing order-book depth", FaultDisposition.Abstain, "Require fresh depth before actionability"),
        Case("MODEL-01", "Model", "Artifact missing", FaultDisposition.Abstain, "Keep the strategy research-only"),
        Case("MODEL-02", "Model", "Feature schema mismatch", FaultDisposition.HaltLane, "Load a schema-compatible artifact"),
        Case("MODEL-03", "Model", "Invalid probability", FaultDisposition.RejectInput, "Reject the forecast contract"),
        Case("MODEL-04", "Model", "Inference timeout", FaultDisposition.Abstain, "Expire the decision cycle"),
        Case("MODEL-05", "Model", "Expert exception", FaultDisposition.Abstain, "Record the failed expert explicitly"),
        Case("BROKER-01", "Broker", "HTTP 429", FaultDisposition.Abstain, "Back off without changing the client order ID"),
        Case("BROKER-02", "Broker", "HTTP 500", FaultDisposition.Abstain, "Retry only after broker-state lookup"),
        Case("BROKER-03", "Broker", "Submit timeout after acceptance", FaultDisposition.RecoverExisting, "Lookup by deterministic client order ID"),
        Case("BROKER-04", "Broker", "Partial fill", FaultDisposition.Reconcile, "Persist fill and manage remaining quantity"),
        Case("BROKER-05", "Broker", "Cancel/fill race", FaultDisposition.Reconcile, "Use final broker quantity as truth"),
        Case("BROKER-06", "Broker", "Duplicate trade update", FaultDisposition.Reconcile, "Apply the update idempotently"),
        Case("BROKER-07", "Broker", "Unknown order", FaultDisposition.HaltLane, "Reconcile orders and positions before retry"),
        Case("RUNTIME-01", "Runtime", "Durable store unavailable", FaultDisposition.HaltLane, "Restore persistence before reservation"),
        Case("POLICY-01", "Policy", "Malformed or injected policy output", FaultDisposition.RejectInput, "Reject untyped policy output"),
    ];

    public static FaultCampaignReport Run(DateTimeOffset? now = null)
    {
        DateTimeOffset started = now ?? DateTimeOffset.UtcNow;
        FaultCampaignCaseResult[] results = Cases.Select(test =>
        {
            FaultDisposition? observed = Observe(test.Id);
            bool passed = observed is { } disposition &&
                disposition == test.ExpectedDisposition &&
                (!test.BrokerMutationAllowed || disposition is FaultDisposition.RecoverExisting
                    or FaultDisposition.Reconcile);

            return new FaultCampaignCaseResult(
                test.Id, test.Category, test.ExpectedDisposition, observed,
                test.BrokerMutationAllowed, observed is not null, passed, test.Recovery);
        }).ToArray();

        return new FaultCampaignReport(
            CampaignId, started, DateTimeOffset.UtcNow, results.Length,
            results.Count(result => result.Exercised),
            results.Count(result => result.Passed),
            results);
    }

    /// <summary>
    /// Runs the driver for one case, or reports that none exists.
    ///
    /// A driver that throws is a failure of the code under test, not of the campaign, so the
    /// exception is contained and reported as the absence of containment.
    /// </summary>
    private static FaultDisposition? Observe(string id)
    {
        if (!Drivers.TryGetValue(id, out Func<FaultDisposition>? driver)) return null;

        try
        {
            return driver();
        }
        catch (Exception)
        {
            // Reaching here means the injection escaped the code that was supposed to contain it.
            // Reporting a disposition that cannot match any expectation records that as a failure
            // rather than letting the campaign crash without a result.
            return FaultDisposition.DegradeReadOnly;
        }
    }

    public static void Save(FaultCampaignReport report, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is not null)
            Directory.CreateDirectory(directory);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(report, JsonOptions));
        File.Move(temporary, path, overwrite: true);
    }

    public static FaultCampaignReport? Load(string path)
    {
        if (!File.Exists(path))
            return null;
        return JsonSerializer.Deserialize<FaultCampaignReport>(File.ReadAllText(path), JsonOptions);
    }

    private static FaultCampaignCase Case(
        string id, string category, string injection, FaultDisposition disposition, string recovery) =>
        new(id, category, injection, disposition,
            disposition is FaultDisposition.RecoverExisting or FaultDisposition.Reconcile, recovery);

    /// <summary>
    /// The injection for each case, driven through the code that runs in production.
    ///
    /// A case absent from this map has no driver and is reported as unexercised. The map is
    /// deliberately incomplete rather than filled with stubs: an entry here is a claim that real
    /// code was run, and a stub returning the expected answer would restore exactly the tautology
    /// this replaced.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, Func<FaultDisposition>> Drivers =
        new Dictionary<string, Func<FaultDisposition>>(StringComparer.Ordinal)
        {
            ["DATA-01"] = CrossedQuote,
            ["DATA-02"] = MissingQuote,
            ["DATA-03"] = StaleQuote,
            ["DATA-04"] = OutOfOrderEvent,
            ["DATA-06"] = NonFiniteTradePrice,
            ["DATA-07"] = MissingOrderBookDepth,
            ["RUNTIME-01"] = DurableStoreUnavailable,
        };

    private const int Slot = 0;

    private static MarketStateStore Store() => new(instrumentCapacity: 4);

    /// <summary>DATA-01: a bid above the ask is not a tradable market, it is a broken message.</summary>
    private static FaultDisposition CrossedQuote()
    {
        ValidationResult result = Store().Apply(Quote(bid: 30_010d, ask: 30_000d, eventNs: 1_000L));
        return result is { IsValid: false, ReasonCode: "CROSSED_QUOTE" }
            ? FaultDisposition.RejectInput
            : FaultDisposition.DegradeReadOnly;
    }

    /// <summary>DATA-02: an instrument nothing has quoted must not read as a healthy zero market.</summary>
    private static FaultDisposition MissingQuote()
    {
        InstrumentSnapshot snapshot = Store().Snapshot(Slot);
        return snapshot.QuoteQuality is DataQuality.Disconnected
            ? FaultDisposition.Abstain
            : FaultDisposition.DegradeReadOnly;
    }

    /// <summary>
    /// DATA-03: after a quote that arrived out of time order, the instrument's quality must say so,
    /// so that everything downstream declines to act until the book refreshes.
    /// </summary>
    private static FaultDisposition StaleQuote()
    {
        MarketStateStore store = Store();
        store.Apply(Quote(bid: 30_000d, ask: 30_010d, eventNs: 2_000L));
        store.Apply(Quote(bid: 29_000d, ask: 29_010d, eventNs: 1_000L));

        return store.Snapshot(Slot).QuoteQuality is DataQuality.Stale
            ? FaultDisposition.Abstain
            : FaultDisposition.DegradeReadOnly;
    }

    /// <summary>
    /// DATA-04: the late event is refused and the newer state survives it. Rejecting while quietly
    /// overwriting the good state with the stale prices would pass a check on the return value and
    /// still leave the runtime deciding on the older market.
    /// </summary>
    private static FaultDisposition OutOfOrderEvent()
    {
        MarketStateStore store = Store();
        store.Apply(Quote(bid: 30_000d, ask: 30_010d, eventNs: 2_000L));
        ValidationResult late = store.Apply(Quote(bid: 29_000d, ask: 29_010d, eventNs: 1_000L));
        InstrumentSnapshot snapshot = store.Snapshot(Slot);

        bool refused = !late.IsValid;
        bool retained = snapshot.Bid.Equals(30_000d) && snapshot.Ask.Equals(30_010d);
        return refused && retained ? FaultDisposition.RejectInput : FaultDisposition.DegradeReadOnly;
    }

    /// <summary>DATA-06: a non-finite price cannot be compared, sized against, or priced.</summary>
    private static FaultDisposition NonFiniteTradePrice()
    {
        ValidationResult result = Store().Apply(new TradeEvent(
            EventId: 1, InstrumentSlot: Slot, Price: double.NaN, Size: 1d,
            EventUnixNanoseconds: 1_000L, ReceiveMonotonicTicks: 0L, SourceSequence: 1));

        return result is { IsValid: false, ReasonCode: "INVALID_TRADE_PRICE" }
            ? FaultDisposition.RejectInput
            : FaultDisposition.DegradeReadOnly;
    }

    /// <summary>
    /// DATA-07: with no depth ever received, the book's quality must remain disconnected rather
    /// than reporting a real but perfectly balanced book, which is what a zero-zero depth looks
    /// like to an imbalance calculation.
    /// </summary>
    private static FaultDisposition MissingOrderBookDepth()
    {
        InstrumentSnapshot snapshot = Store().Snapshot(Slot);
        return snapshot.OrderBookQuality is DataQuality.Disconnected
            ? FaultDisposition.Abstain
            : FaultDisposition.DegradeReadOnly;
    }

    /// <summary>
    /// RUNTIME-01: when the durable store cannot be reached, the lane stops. Reserving an execution
    /// that cannot be persisted is how a restart loses track of an open position.
    /// </summary>
    private static FaultDisposition DurableStoreUnavailable()
    {
        // A path under a file, which no filesystem will open as a directory.
        //
        // The predicate driven here is the one production actually gates on: the diagnostic lane
        // refuses with PERSISTENCE_UNAVAILABLE and the multi-leg lifecycle declines to start, both
        // on IsAvailable(). Reading the store instead would prove nothing -- a read of a store that
        // does not exist returns an empty list quite happily, which is how this driver first
        // reported the fault uncontained.
        string occupied = Path.Combine(Path.GetTempPath(), $"quantdesk-fault-{Guid.NewGuid():N}");
        File.WriteAllText(occupied, "occupied");
        try
        {
            var store = new SpotExecutionStore(Path.Combine(occupied, "executions.json"));
            return store.IsAvailable() ? FaultDisposition.DegradeReadOnly : FaultDisposition.HaltLane;
        }
        finally
        {
            File.Delete(occupied);
        }
    }

    private static QuoteEvent Quote(double bid, double ask, long eventNs) => new(
        EventId: 1, InstrumentSlot: Slot, Bid: bid, Ask: ask, BidSize: 1d, AskSize: 1d,
        EventUnixNanoseconds: eventNs, ReceiveMonotonicTicks: 0L, SourceSequence: 1);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
}
