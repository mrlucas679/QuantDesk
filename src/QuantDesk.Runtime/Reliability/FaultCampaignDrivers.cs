using QuantDesk.Domain.Contracts;
using QuantDesk.Domain.Execution;
using QuantDesk.Domain.Experts;
using QuantDesk.Domain.Forecasts;
using QuantDesk.Domain.Runtime;
using QuantDesk.Runtime.Execution;
using QuantDesk.Runtime.Experts;
using QuantDesk.Runtime.Ingestion;
using QuantDesk.Runtime.Persistence;
using QuantDesk.Runtime.Research;
using QuantDesk.Runtime.State;
using QuantDesk.Runtime.Time;

namespace QuantDesk.Runtime.Reliability;

/// <summary>
/// The model, broker, stream and policy injections, driven through production code.
///
/// Kept apart from <see cref="FaultCampaign"/> because these need fault-injecting collaborators and
/// the case table should stay readable. The collaborators below are injectors, not mocks: the
/// campaign's entire purpose is to make a failure happen and watch what contains it, and a broker
/// that returns 429 on demand is the fault, not a stand-in for one.
///
/// Everything they drive is the code that runs in production -- <see cref="SpotExecutionLifecycle"/>
/// with its real durable store, <see cref="FittedModelContract.Validate"/>, the real
/// <see cref="MarketStateStore"/>. Nothing here re-implements a decision in order to check it.
/// </summary>
internal static class FaultCampaignDrivers
{
    // ------------------------------------------------------------------------------- data

    /// <summary>
    /// DATA-05: the feed dropped and came back, so an unknown number of updates were missed.
    ///
    /// This venue publishes no usable sequence number -- measured over a live session, every
    /// consecutive order-book sequence delta was zero and trades carried what looked like hashed
    /// ids -- so a lost message cannot be detected from the messages. A disconnection is the only
    /// evidence of loss the feed gives, and after one the book on hand is wrong by an unknown
    /// amount rather than merely old.
    /// </summary>
    internal static FaultDisposition SequenceGap()
    {
        var store = new MarketStateStore(instrumentCapacity: 4);
        store.Apply(new QuoteEventFixture(30_000d, 30_010d, 2_000L).Event);

        store.MarkStreamInterrupted();

        return store.Snapshot(0).QuoteQuality is DataQuality.GapDetected
            ? FaultDisposition.HaltLane
            : FaultDisposition.DegradeReadOnly;
    }

    // ------------------------------------------------------------------------------ model

    /// <summary>
    /// MODEL-01: nothing has been adopted, so the store reports unfitted and the lane abstains.
    ///
    /// The failure this guards is a store that returns a default-constructed model whose parameters
    /// happen to be zero. That produces confident numbers from no evidence at all.
    /// </summary>
    internal static FaultDisposition ArtifactMissing()
    {
        var store = new FittedModelStore();
        return !store.Har("BTC/USD", 5).IsFitted && !store.Garch("BTC/USD", 5).IsFitted
            ? FaultDisposition.Abstain
            : FaultDisposition.DegradeReadOnly;
    }

    /// <summary>
    /// MODEL-02: the runtime computes a different feature set than the model was fitted on.
    ///
    /// Fatal by design and not a warning: a model fed features in an order it was not fitted on
    /// produces confident numbers from the wrong coefficients, and nothing downstream can tell.
    /// </summary>
    internal static FaultDisposition FeatureSchemaMismatch()
    {
        FittedModelRejection rejection = Contract("fitted-on-this-schema").Validate(
            RuntimeFeatureContract.SchemaOnly("runtime-computes-something-else"),
            SupportedModelTypes);

        return rejection is FittedModelRejection.FeatureSchemaMismatch
            ? FaultDisposition.HaltLane
            : FaultDisposition.DegradeReadOnly;
    }

    /// <summary>
    /// MODEL-03: a parameter that is not a finite number reaches the contract.
    ///
    /// A NaN weight is how an "invalid probability" actually arrives here -- it survives arithmetic,
    /// compares false against every bound, and turns a gate that looks like a range check into one
    /// that passes everything.
    /// </summary>
    internal static FaultDisposition InvalidProbability()
    {
        const string Schema = "shared-schema";
        FittedModelContract contract = Contract(Schema) with
        {
            Parameters = new Dictionary<string, double> { ["state_probability"] = double.NaN },
        };

        return contract.Validate(RuntimeFeatureContract.SchemaOnly(Schema), SupportedModelTypes)
            is FittedModelRejection.UnusableParameters
            ? FaultDisposition.RejectInput
            : FaultDisposition.DegradeReadOnly;
    }

    /// <summary>
    /// MODEL-04: the forecast did not arrive before it stopped being about the current market.
    ///
    /// Driven through the real committee: a vote whose validity window has closed is counted stale
    /// and the family refuses. That is what "expire the decision cycle" means in practice -- not a
    /// cancelled task, but evidence that is no longer allowed to inform a decision. A committee that
    /// aggregated it anyway would trade the market of several seconds ago while every count still
    /// looked healthy.
    /// </summary>
    internal static FaultDisposition InferenceTimeout()
    {
        const long ValidUntil = 1_000L;
        const long Now = 5_000L;

        ForecastFamilyDecision<VolatilityForecast> decision = new TypedForecastCommittee()
            .EvaluateVolatility(
                instrumentSlot: 0,
                votes: [Vote(ForecastStatus.Valid, validUntilMonotonicTicks: ValidUntil)],
                nowMonotonicTicks: Now,
                sourceStateVersion: 1,
                expectedExperts: 1);

        return !decision.HasForecast && decision.Availability.Stale == 1
            ? FaultDisposition.Abstain
            : FaultDisposition.DegradeReadOnly;
    }

    /// <summary>
    /// MODEL-05: an expert failed, and the committee records which rather than quietly proceeding.
    ///
    /// The harm in silence is not the missing expert, it is the availability ratio: an expert that
    /// disappears from the denominator makes a one-expert consensus look like a full one. Here the
    /// failure has to show up in the count as well as in the refusal.
    /// </summary>
    internal static FaultDisposition ExpertException()
    {
        ForecastFamilyDecision<VolatilityForecast> decision = new TypedForecastCommittee()
            .EvaluateVolatility(
                instrumentSlot: 0,
                votes: [Vote(ForecastStatus.Failed, validUntilMonotonicTicks: 10_000L)],
                nowMonotonicTicks: 1_000L,
                sourceStateVersion: 1,
                expectedExperts: 1);

        return !decision.HasForecast
            && decision.Availability.Failed == 1
            && decision.Availability.Expected == 1
            ? FaultDisposition.Abstain
            : FaultDisposition.DegradeReadOnly;
    }

    /// <summary>One expert's volatility vote, in whatever status the case needs to inject.</summary>
    private static ForecastVote<VolatilityForecast> Vote(
        ForecastStatus status, long validUntilMonotonicTicks) =>
        new(
            ExpertId: 1,
            new VolatilityForecast(
                new ForecastMetadata(
                    ExpertId: 1,
                    InstrumentSlot: 0,
                    Type: ForecastType.RealizedVolatility,
                    Horizon: TimeSpan.FromMinutes(5),
                    GeneratedEventNs: 1_000L,
                    GeneratedMonotonicTicks: 0L,
                    ValidUntilMonotonicTicks: validUntilMonotonicTicks,
                    SourceStateVersion: 1,
                    ModelVersion: 1,
                    Status: status),
                ExpectedRealizedVariance: 0.0004,
                ExpectedAnnualizedVolatility: 0.6,
                ForecastVariance: 0.0001,
                CalibrationScore: 0.9),
            Weight: 1d);

    // ----------------------------------------------------------------------------- broker

    /// <summary>
    /// BROKER-01: the venue rate-limits the submission.
    ///
    /// What must survive is the client order id. Backing off is fine; minting a fresh id on the
    /// retry is how one opportunity becomes two positions, because the second submission cannot be
    /// recognised as the first one returning.
    /// </summary>
    internal static FaultDisposition RateLimited() => SubmitFaultKeepsIdentity(
        new FaultBroker { SubmitThrows = new HttpRequestExceptionLike("429") });

    /// <summary>BROKER-02: the venue fails, and any retry must follow a broker-state lookup.</summary>
    internal static FaultDisposition ServerError()
    {
        var broker = new FaultBroker { SubmitThrows = new HttpRequestExceptionLike("500") };
        FaultDisposition disposition = SubmitFaultKeepsIdentity(broker);

        // The lookup is the point. A blind retry after a 500 duplicates an order that may well have
        // been accepted before the error was returned.
        return disposition is FaultDisposition.Abstain && broker.LookupCount > 0
            ? FaultDisposition.Abstain
            : FaultDisposition.DegradeReadOnly;
    }

    /// <summary>
    /// BROKER-03: the submission times out after the venue already accepted it.
    ///
    /// The worst broker fault there is: the order exists, this process does not know it, and a
    /// retry would open a second position. Recovery has to find the first one, and it can only do
    /// that because the client order id was derived from the opportunity rather than generated.
    /// </summary>
    internal static FaultDisposition SubmitTimeoutAfterAcceptance()
    {
        const string ExecutionId = "fault-broker-03";
        string entryId = DeterministicClientOrderId.Create("spot", ExecutionId, "entry");

        var broker = new FaultBroker
        {
            SubmitThrows = new TimeoutException("submit timed out"),

            // The venue took it. This is the state the timeout hid.
            Existing = new BrokerOrderSnapshot("broker-1", entryId, "accepted", 0m, null),
        };

        SpotExecutionRecord? record = RunLifecycle(broker, ExecutionId);
        if (record is null) return FaultDisposition.DegradeReadOnly;

        bool recovered = record.EntryBrokerOrderId == "broker-1"
            || record.State is SpotExecutionState.EntryAccepted or SpotExecutionState.EntrySubmitted;

        return recovered && broker.SubmitCount == 1
            ? FaultDisposition.RecoverExisting
            : FaultDisposition.DegradeReadOnly;
    }

    /// <summary>BROKER-04: part of the order filled, and the remainder is still live.</summary>
    internal static FaultDisposition PartialFill() => FillIsReconciled(
        new BrokerOrderSnapshot("broker-1", string.Empty, "partially_filled", 0.4m, 30_000m),
        expectedFilled: 0.4m);

    /// <summary>
    /// BROKER-05: a cancel and a fill cross, and the broker's final quantity is the truth.
    ///
    /// Believing the cancel here leaves a real position nothing is tracking.
    /// </summary>
    internal static FaultDisposition CancelFillRace() => FillIsReconciled(
        new BrokerOrderSnapshot("broker-1", string.Empty, "filled", 1m, 30_000m),
        expectedFilled: 1m);

    /// <summary>
    /// BROKER-06: the same update arrives twice, and applying it twice must not double the fill.
    ///
    /// Stream redelivery is ordinary. A quantity that accumulates per message rather than being set
    /// from broker truth turns an ordinary redelivery into a position twice the intended size.
    /// </summary>
    internal static FaultDisposition DuplicateTradeUpdate()
    {
        var snapshot = new BrokerOrderSnapshot("broker-1", string.Empty, "filled", 1m, 30_000m);
        var broker = new FaultBroker { Existing = snapshot, AcknowledgeSubmit = true };

        const string ExecutionId = "fault-broker-06";
        using var scope = new StoreScope();
        SpotExecutionLifecycle lifecycle = Lifecycle(broker, scope.Store);

        Reserve(lifecycle, ExecutionId);
        broker.Existing = snapshot with { ClientOrderId = EntryIdOf(ExecutionId) };

        lifecycle.AdvanceAsync(ExecutionId, CancellationToken.None).GetAwaiter().GetResult();
        SpotExecutionRecord first = lifecycle
            .AdvanceAsync(ExecutionId, CancellationToken.None).GetAwaiter().GetResult();

        // The same broker truth, applied again.
        SpotExecutionRecord second = lifecycle
            .AdvanceAsync(ExecutionId, CancellationToken.None).GetAwaiter().GetResult();

        return first.EntryFilledQuantity == second.EntryFilledQuantity
            && second.EntryFilledQuantity <= 1m
            ? FaultDisposition.Reconcile
            : FaultDisposition.DegradeReadOnly;
    }

    /// <summary>
    /// BROKER-07: the venue does not know the order this process believes it submitted.
    ///
    /// Orders and positions have to be reconciled before anything else is sent, because the two
    /// explanations -- never arrived, or arrived and filled -- call for opposite actions.
    /// </summary>
    internal static FaultDisposition UnknownOrder()
    {
        var broker = new FaultBroker { AcknowledgeSubmit = true, Existing = null };
        const string ExecutionId = "fault-broker-07";

        using var scope = new StoreScope();
        SpotExecutionLifecycle lifecycle = Lifecycle(broker, scope.Store);
        Reserve(lifecycle, ExecutionId);

        lifecycle.AdvanceAsync(ExecutionId, CancellationToken.None).GetAwaiter().GetResult();
        SpotExecutionRecord tracked = lifecycle
            .AdvanceAsync(ExecutionId, CancellationToken.None).GetAwaiter().GetResult();

        // Not filled, not advanced past submission, and the lookup happened. The lane holds.
        bool held = broker.LookupCount > 0
            && tracked.EntryFilledQuantity == 0m
            && tracked.State is not SpotExecutionState.EntryFilled and not SpotExecutionState.Holding;

        return held ? FaultDisposition.HaltLane : FaultDisposition.DegradeReadOnly;
    }

    // ----------------------------------------------------------------------------- policy

    /// <summary>
    /// POLICY-01: a policy output that is not the typed thing the runtime reads.
    ///
    /// Section 10.1 exists because an untyped decision -- free text, a loosely parsed blob, anything
    /// a language model produced -- cannot be range-checked, unit-checked or refused. Reading one
    /// is how an injected instruction becomes an order.
    /// </summary>
    internal static FaultDisposition MalformedPolicyOutput()
    {
        const string Malformed = "{\"action\":\"BUY\",\"size\":\"as much as possible\"}";

        try
        {
            FittedModelArtifactReader.Read(Malformed);
            return FaultDisposition.DegradeReadOnly;
        }
        catch (Exception exception) when (exception is InvalidDataException or System.Text.Json.JsonException)
        {
            return FaultDisposition.RejectInput;
        }
    }

    // -------------------------------------------------------------------------- machinery

    private static readonly IReadOnlySet<string> SupportedModelTypes =
        new HashSet<string>(StringComparer.Ordinal) { "har_variance", "garch_variance" };

    private static readonly DateTimeOffset Start =
        new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    private static FittedModelContract Contract(string featureSchemaHash) => new(
        ArtifactId: "artifact-1",
        ModelId: "model-1",
        ModelType: "har_variance",
        ModelVersion: "v1",
        FeatureSchemaHash: featureSchemaHash,
        DatasetHash: "dataset-1",
        Parameters: new Dictionary<string, double> { ["intercept"] = 0.1 },
        RandomSeed: 0,
        EvidenceGrade: "A_Direct",
        PromotionState: "VALIDATED",
        GitCommit: "commit-1",
        CreatedAt: Start);

    private static string EntryIdOf(string executionId) =>
        DeterministicClientOrderId.Create("spot", executionId, "entry");

    private static SpotExecutionLifecycle Lifecycle(FaultBroker broker, SpotExecutionStore store) =>
        new(broker, store, new VirtualRuntimeClock(Start), TimeSpan.FromSeconds(2));

    private static bool Reserve(SpotExecutionLifecycle lifecycle, string executionId) =>
        lifecycle.TryReserve(
            executionId, "fault-campaign", "BTC/USD", 0, quantity: 1m,
            definedMaximumLoss: 100m, maximumHoldingPeriod: TimeSpan.FromMinutes(5));

    /// <summary>Runs one execution to its first broker interaction and returns the record.</summary>
    private static SpotExecutionRecord? RunLifecycle(FaultBroker broker, string executionId)
    {
        using var scope = new StoreScope();
        SpotExecutionLifecycle lifecycle = Lifecycle(broker, scope.Store);
        if (!Reserve(lifecycle, executionId)) return null;

        return lifecycle.AdvanceAsync(executionId, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>
    /// A submission that failed leaves the opportunity's identity untouched and opens nothing.
    /// </summary>
    private static FaultDisposition SubmitFaultKeepsIdentity(FaultBroker broker)
    {
        const string ExecutionId = "fault-broker-submit";
        string expected = EntryIdOf(ExecutionId);

        SpotExecutionRecord? record = RunLifecycle(broker, ExecutionId);
        if (record is null) return FaultDisposition.DegradeReadOnly;

        bool identityHeld = string.Equals(record.EntryClientOrderId, expected, StringComparison.Ordinal);
        bool nothingOpened = record.EntryFilledQuantity == 0m;

        // Exactly one, not at most one. A lifecycle that never reached the broker would satisfy
        // every other clause here and report the fault contained without the fault ever having
        // been delivered -- a vacuous pass, which is the failure mode this whole rewrite exists to
        // remove.
        bool submittedOnce = broker.SubmitCount == 1;

        return identityHeld && nothingOpened && submittedOnce
            ? FaultDisposition.Abstain
            : FaultDisposition.DegradeReadOnly;
    }

    /// <summary>Broker truth about a fill is persisted as the record's quantity.</summary>
    private static FaultDisposition FillIsReconciled(
        BrokerOrderSnapshot snapshot, decimal expectedFilled)
    {
        string executionId = $"fault-fill-{expectedFilled}";
        var broker = new FaultBroker { AcknowledgeSubmit = true };

        using var scope = new StoreScope();
        SpotExecutionLifecycle lifecycle = Lifecycle(broker, scope.Store);
        if (!Reserve(lifecycle, executionId)) return FaultDisposition.DegradeReadOnly;

        broker.Existing = snapshot with { ClientOrderId = EntryIdOf(executionId) };

        lifecycle.AdvanceAsync(executionId, CancellationToken.None).GetAwaiter().GetResult();
        SpotExecutionRecord tracked = lifecycle
            .AdvanceAsync(executionId, CancellationToken.None).GetAwaiter().GetResult();

        return tracked.EntryFilledQuantity == expectedFilled
            ? FaultDisposition.Reconcile
            : FaultDisposition.DegradeReadOnly;
    }

    /// <summary>A durable store on a real path, removed afterwards.</summary>
    private sealed class StoreScope : IDisposable
    {
        private readonly string _path = Path.Combine(
            Path.GetTempPath(), $"quantdesk-fault-{Guid.NewGuid():N}.json");

        public SpotExecutionStore Store => field ??= new SpotExecutionStore(_path);

        public void Dispose()
        {
            if (File.Exists(_path)) File.Delete(_path);
        }
    }

    /// <summary>An exception shaped like the venue's transport failures, without the dependency.</summary>
    private sealed class HttpRequestExceptionLike(string status)
        : IOException($"Broker returned HTTP {status}.");

    /// <summary>
    /// A broker that fails on demand.
    ///
    /// An injector rather than a mock: the campaign exists to make the failure happen. It counts
    /// submissions and lookups because the properties under test are about how many of each the
    /// lifecycle performs, not only about what it ends up believing.
    /// </summary>
    private sealed class FaultBroker : IBrokerExecutionGateway
    {
        public Exception? SubmitThrows { get; init; }
        public bool AcknowledgeSubmit { get; init; }
        public BrokerOrderSnapshot? Existing { get; set; }

        public int SubmitCount { get; private set; }
        public int LookupCount { get; private set; }

        public bool IsPaperEnvironment => true;

        public Task<BrokerSubmitResult> SubmitAsync(
            ExecutionCommand command, CancellationToken cancellationToken)
        {
            SubmitCount++;
            if (SubmitThrows is not null) throw SubmitThrows;

            return Task.FromResult(AcknowledgeSubmit
                ? new BrokerSubmitResult(BrokerSubmitState.Acknowledged, "broker-1", null, null)
                : new BrokerSubmitResult(BrokerSubmitState.Unknown, null, "UNKNOWN", null));
        }

        public Task<BrokerOrderSnapshot?> FindByClientOrderIdAsync(
            string clientOrderId, CancellationToken cancellationToken)
        {
            LookupCount++;
            return Task.FromResult(
                Existing is not null
                && string.Equals(Existing.ClientOrderId, clientOrderId, StringComparison.Ordinal)
                    ? Existing
                    : null);
        }
    }

    /// <summary>A well-formed quote, so a DATA driver's setup cannot be the thing that fails.</summary>
    private readonly struct QuoteEventFixture(double bid, double ask, long eventNs)
    {
        public Domain.Market.QuoteEvent Event { get; } = new(
            EventId: 1, InstrumentSlot: 0, Bid: bid, Ask: ask, BidSize: 1d, AskSize: 1d,
            EventUnixNanoseconds: eventNs, ReceiveMonotonicTicks: 0L, SourceSequence: 0);
    }
}
