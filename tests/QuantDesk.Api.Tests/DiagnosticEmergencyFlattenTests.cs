using QuantDesk.Alpaca.Mapping;
using QuantDesk.Api.PaperTrading;
using QuantDesk.Domain.Execution;
using QuantDesk.Domain.Trading;
using QuantDesk.Runtime.Persistence;
using QuantDesk.Runtime.Time;

namespace QuantDesk.Api.Tests;

/// <summary>
/// The last-resort path, tested on its own now that it no longer lives inside a service that also opens
/// positions.
///
/// What is being pinned here is an ordering, not a set of return values. The flatten reads broker truth,
/// cancels its own working orders, confirms they are gone, and only then sizes and sends — and it must be
/// safe to run again after a crash at any point in that sequence. Each test names the step it protects.
/// </summary>
public sealed class DiagnosticEmergencyFlattenTests : IDisposable
{
    private const string ExperimentId = "EXP-FLATTEN";
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly string _directory;
    private readonly DiagnosticExecutionStore _store;
    private readonly StubBroker _broker = new();
    private readonly DiagnosticEmergencyFlatten _flatten;

    public DiagnosticEmergencyFlattenTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"quantdesk-flatten-{Guid.NewGuid():N}");
        _store = new DiagnosticExecutionStore(Path.Combine(_directory, "execution.json"));
        _flatten = new DiagnosticEmergencyFlatten(
            _store,
            _broker,
            new DictionaryInstrumentSymbolResolver(
                new Dictionary<int, string> { [0] = DiagnosticExecutionOptions.RequiredSymbol }),
            new VirtualRuntimeClock(Now));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task AnUnknownExperimentIsRefusedWithoutTouchingTheBroker()
    {
        DiagnosticEmergencyFlattenResult result = await _flatten.FlattenAsync("missing", CancellationToken.None);

        Assert.Equal(DiagnosticEmergencyFlattenOutcome.Refused, result.Outcome);
        Assert.Equal("DIAGNOSTIC_NOT_FOUND", result.ReasonCode);
        Assert.Equal(0, _broker.SubmitCount);
    }

    [Fact]
    public async Task ALiveEnvironmentIsRefusedBeforeAnythingIsSent()
    {
        Given();
        _broker.IsPaperEnvironment = false;

        DiagnosticEmergencyFlattenResult result = await FlattenAsync();

        Assert.Equal("ALPACA_PAPER_REQUIRED", result.ReasonCode);
        Assert.Equal(0, _broker.SubmitCount);
    }

    [Fact]
    public async Task ARecordWithoutADeterministicClientIdIsRefusedRatherThanSentBlind()
    {
        // Without a recomputable ID an ambiguous submission could never be resolved, so a flatten that
        // cannot be looked up afterwards must not be sent at all.
        Given(record => record with { EmergencyClientOrderId = null });

        DiagnosticEmergencyFlattenResult result = await FlattenAsync();

        Assert.Equal("EMERGENCY_CLIENT_ID_UNAVAILABLE", result.ReasonCode);
        Assert.Equal(0, _broker.SubmitCount);
    }

    [Fact]
    public async Task AnExistingFlattenOrderIsResumedRatherThanDuplicated()
    {
        // The restart case. A second flatten would sell a position that is already being sold.
        Given();
        _broker.Existing = Order("emergency-id", "accepted", filledQuantity: 0m);
        _broker.Positions = [Position(0.5m)];

        DiagnosticEmergencyFlattenResult result = await FlattenAsync();

        Assert.Equal(DiagnosticEmergencyFlattenOutcome.Working, result.Outcome);
        Assert.Equal(0, _broker.SubmitCount);
        Assert.Equal("EmergencyFlattenAccepted", Persisted().State);
    }

    [Fact]
    public async Task AFilledFlattenReportsFlatSoTheCallerReconciles()
    {
        Given();
        _broker.Existing = Order("emergency-id", "filled", filledQuantity: 0.5m);

        DiagnosticEmergencyFlattenResult result = await FlattenAsync();

        Assert.Equal(DiagnosticEmergencyFlattenOutcome.Flat, result.Outcome);
        Assert.Equal("Reconciling", Persisted().State);
        Assert.Equal(0.5m, Persisted().EmergencyFilledQuantity);
    }

    [Theory]
    [InlineData("rejected", "EMERGENCY_REJECTED")]
    [InlineData("canceled", "EMERGENCY_CANCELED")]
    [InlineData("expired", "EMERGENCY_EXPIRED")]
    public async Task AFlattenOrderThatEndedWithoutClosingIsATerminalFailure(string status, string expected)
    {
        Given();
        _broker.Existing = Order("emergency-id", status, filledQuantity: 0m);

        DiagnosticEmergencyFlattenResult result = await FlattenAsync();

        Assert.Equal(DiagnosticEmergencyFlattenOutcome.Failed, result.Outcome);
        Assert.Equal(expected, result.ReasonCode);
        Assert.Equal("EmergencyFlattenFailed", Persisted().State);
    }

    [Fact]
    public async Task ThisExperimentsWorkingOrdersAreCancelledBeforeTheFlattenIsSized()
    {
        // A working entry could fill while the flatten is being sized, and the flatten would then close
        // the wrong quantity.
        Given();
        _broker.OpenOrders = [Order("entry-id", "new", 0m)];
        _broker.Positions = [Position(0.4m)];

        DiagnosticEmergencyFlattenResult result = await FlattenAsync();

        Assert.Equal(DiagnosticEmergencyFlattenOutcome.Working, result.Outcome);
        Assert.Equal(1, _broker.CancelCount);
        Assert.Equal(0.4m, _broker.LastCommand!.Quantity);
    }

    [Fact]
    public async Task ACancelThatCannotBeConfirmedFailsInsteadOfProceeding()
    {
        Given();
        _broker.OpenOrders = [Order("entry-id", "new", 0m)];
        _broker.HonourCancels = false;
        _broker.Positions = [Position(0.4m)];

        DiagnosticEmergencyFlattenResult result = await FlattenAsync();

        Assert.Equal(DiagnosticEmergencyFlattenOutcome.Failed, result.Outcome);
        Assert.Equal("EMERGENCY_CANCEL_UNCONFIRMED", result.ReasonCode);
        Assert.Equal(0, _broker.SubmitCount);
    }

    [Fact]
    public async Task AnAlreadyFlatPositionIsReportedFlatWithoutSendingAnything()
    {
        Given();
        _broker.Positions = [];

        DiagnosticEmergencyFlattenResult result = await FlattenAsync();

        Assert.Equal(DiagnosticEmergencyFlattenOutcome.Flat, result.Outcome);
        Assert.Equal(0, _broker.SubmitCount);
        Assert.Equal("Reconciling", Persisted().State);
    }

    [Fact]
    public async Task AShortPositionIsRefusedBecauseTheSellThisLaneSendsWouldDeepenIt()
    {
        Given();
        _broker.Positions = [Position(-0.4m)];

        DiagnosticEmergencyFlattenResult result = await FlattenAsync();

        Assert.Equal(DiagnosticEmergencyFlattenOutcome.Failed, result.Outcome);
        Assert.Equal("EMERGENCY_SHORT_EXPOSURE_UNSUPPORTED", result.ReasonCode);
        Assert.Equal(0, _broker.SubmitCount);
    }

    [Fact]
    public async Task TheFlattenIsSizedToBrokerTruthRatherThanToWhatTheRecordBelieved()
    {
        // The record's own fill quantity can be stale; the position is what actually has to be closed.
        Given(record => record with { EntryFilledQuantity = 0.1m });
        _broker.Positions = [Position(0.37m)];

        await FlattenAsync();

        Assert.Equal(0.37m, _broker.LastCommand!.Quantity);
        Assert.Equal(OrderSide.Sell, _broker.LastCommand.Side);
        Assert.Equal(ExecutionPriority.EmergencyExit, _broker.LastCommand.Priority);
    }

    [Fact]
    public async Task ASecondAttemptAfterAnUnresolvedSubmissionFailsRatherThanSendingAgain()
    {
        // The claim is single-shot on purpose: a duplicate flatten would open a short position in the
        // act of trying to close a long one.
        Given(record => record with { EmergencySubmissionAttemptedAt = Now.AddMinutes(-1) });
        _broker.Positions = [Position(0.4m)];

        DiagnosticEmergencyFlattenResult result = await FlattenAsync();

        Assert.Equal(DiagnosticEmergencyFlattenOutcome.Failed, result.Outcome);
        Assert.Equal("EMERGENCY_SUBMISSION_UNKNOWN", result.ReasonCode);
        Assert.Equal(0, _broker.SubmitCount);
    }

    [Fact]
    public async Task AnAmbiguousSubmissionIsResolvedByLookupNotByResending()
    {
        Given();
        _broker.Positions = [Position(0.4m)];
        _broker.SubmitBehavior = _ => throw new TimeoutException("the venue did not answer");
        _broker.LookupAfterSubmit = Order("emergency-id", "filled", 0.4m);

        DiagnosticEmergencyFlattenResult result = await FlattenAsync();

        Assert.Equal(DiagnosticEmergencyFlattenOutcome.Flat, result.Outcome);
        Assert.Equal(1, _broker.SubmitCount);
        Assert.Equal(0.4m, Persisted().EmergencyFilledQuantity);
    }

    [Fact]
    public async Task AnAmbiguousSubmissionThatCannotBeFoundFailsRatherThanRetrying()
    {
        Given();
        _broker.Positions = [Position(0.4m)];
        _broker.SubmitBehavior = _ => throw new TimeoutException("the venue did not answer");

        DiagnosticEmergencyFlattenResult result = await FlattenAsync();

        Assert.Equal(DiagnosticEmergencyFlattenOutcome.Failed, result.Outcome);
        Assert.Equal("EMERGENCY_SUBMISSION_UNKNOWN", result.ReasonCode);
        Assert.Equal(1, _broker.SubmitCount);
    }

    [Fact]
    public async Task AVenueRejectionIsRecordedWithTheVenuesOwnReason()
    {
        Given();
        _broker.Positions = [Position(0.4m)];
        _broker.SubmitBehavior = _ => Task.FromResult(
            new BrokerSubmitResult(BrokerSubmitState.Rejected, null, "INSUFFICIENT_POSITION", null));

        DiagnosticEmergencyFlattenResult result = await FlattenAsync();

        Assert.Equal("INSUFFICIENT_POSITION", result.ReasonCode);
        Assert.Equal("INSUFFICIENT_POSITION", Persisted().FailureReason);
    }

    [Fact]
    public async Task ABrokerOutageIsRefusedSoTheAttemptCanBeMadeAgain()
    {
        // Refused, not Failed: nothing was established, so the record must stay resumable.
        Given();
        _broker.ReadsAvailable = false;

        DiagnosticEmergencyFlattenResult result = await FlattenAsync();

        Assert.Equal(DiagnosticEmergencyFlattenOutcome.Refused, result.Outcome);
        Assert.Equal("RECONCILIATION_UNAVAILABLE", result.ReasonCode);
        Assert.Equal("EntryFilled", Persisted().State);
    }

    private Task<DiagnosticEmergencyFlattenResult> FlattenAsync() =>
        _flatten.FlattenAsync(ExperimentId, CancellationToken.None);

    private DiagnosticExecutionRecord Persisted() => _store.Find(ExperimentId)!;

    private void Given(Func<DiagnosticExecutionRecord, DiagnosticExecutionRecord>? adjust = null)
    {
        var record = new DiagnosticExecutionRecord(
            ExperimentId,
            nameof(OrderClassification.DiagnosticExecution),
            DiagnosticExecutionOptions.RequiredSymbol,
            "EntryFilled",
            RequestedNotional: 10m,
            HoldingDuration: TimeSpan.FromMinutes(2),
            CreatedAt: Now,
            EntryClientOrderId: "entry-id",
            ExitClientOrderId: "exit-id")
        {
            EmergencyClientOrderId = "emergency-id",
            EntryFilledQuantity = 0.4m
        };
        _store.Record(adjust is null ? record : adjust(record));
    }

    private static BrokerOrderSnapshot Order(string clientOrderId, string status, decimal filledQuantity) =>
        new($"broker-{clientOrderId}", clientOrderId, status, filledQuantity, null)
        {
            Symbol = DiagnosticExecutionOptions.RequiredSymbol
        };

    private static BrokerPositionSnapshot Position(decimal quantity) =>
        new(DiagnosticExecutionOptions.RequiredSymbol, 0, quantity, 100m);

    /// <summary>
    /// A broker that answers only what this sub-lifecycle asks. Cancels remove the order from the open
    /// list by default, so a test can model an unconfirmed cancel simply by switching that off.
    /// </summary>
    private sealed class StubBroker : IBrokerExecutionGateway
    {
        public bool IsPaperEnvironment { get; set; } = true;
        public bool ReadsAvailable { get; set; } = true;
        public bool HonourCancels { get; set; } = true;
        public int SubmitCount { get; private set; }
        public int CancelCount { get; private set; }
        public ExecutionCommand? LastCommand { get; private set; }
        public BrokerOrderSnapshot? Existing { get; set; }
        public BrokerOrderSnapshot? LookupAfterSubmit { get; set; }
        public Func<ExecutionCommand, Task<BrokerSubmitResult>>? SubmitBehavior { get; set; }
        public IReadOnlyList<BrokerOrderSnapshot> OpenOrders { get; set; } = [];
        public IReadOnlyList<BrokerPositionSnapshot> Positions { get; set; } = [];

        public Task<BrokerAccountSnapshot?> GetAccountAsync(CancellationToken cancellationToken) =>
            Task.FromResult<BrokerAccountSnapshot?>(
                new("paper", "ACTIVE", 100_000m, 100_000m, false, false) { CryptoTradingStatus = "ACTIVE" });

        public Task<BrokerAssetSnapshot?> GetAssetAsync(string symbol, CancellationToken cancellationToken) =>
            Task.FromResult<BrokerAssetSnapshot?>(
                new(DiagnosticExecutionOptions.RequiredSymbol, "active", "crypto", true));

        public Task<BrokerSubmitResult> SubmitAsync(ExecutionCommand command, CancellationToken cancellationToken)
        {
            SubmitCount++;
            LastCommand = command;
            return SubmitBehavior?.Invoke(command) ?? Task.FromResult(
                new BrokerSubmitResult(BrokerSubmitState.Acknowledged, $"broker-{SubmitCount}", null, null));
        }

        public Task<BrokerOrderSnapshot?> FindByClientOrderIdAsync(
            string clientOrderId, CancellationToken cancellationToken) =>
            Task.FromResult(SubmitCount == 0 ? Existing : LookupAfterSubmit);

        public Task<IReadOnlyList<BrokerOrderSnapshot>> ListOpenOrdersForSymbolAsync(
            string symbol, CancellationToken cancellationToken) => ReadsAvailable
            ? Task.FromResult(OpenOrders)
            : Task.FromException<IReadOnlyList<BrokerOrderSnapshot>>(
                new HttpRequestException("simulated broker outage"));

        public Task<IReadOnlyList<BrokerPositionSnapshot>> ListPositionsAsync(CancellationToken cancellationToken) =>
            ReadsAvailable
                ? Task.FromResult(Positions)
                : Task.FromException<IReadOnlyList<BrokerPositionSnapshot>>(
                    new HttpRequestException("simulated broker outage"));

        public Task<BrokerSubmitResult> CancelAsync(string brokerOrderId, CancellationToken cancellationToken)
        {
            CancelCount++;
            if (HonourCancels)
                OpenOrders = [.. OpenOrders.Where(order => order.BrokerOrderId != brokerOrderId)];
            return Task.FromResult(new BrokerSubmitResult(BrokerSubmitState.Acknowledged, brokerOrderId, null, null));
        }
    }
}
