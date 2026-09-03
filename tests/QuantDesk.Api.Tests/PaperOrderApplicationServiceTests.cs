using QuantDesk.Alpaca.Mapping;
using QuantDesk.Api.PaperTrading;
using QuantDesk.Domain.Execution;
using QuantDesk.Domain.Runtime;
using QuantDesk.Domain.Trading;
using QuantDesk.Runtime.Modes;
using QuantDesk.Runtime.Time;

namespace QuantDesk.Api.Tests;

public sealed class PaperOrderApplicationServiceTests
{
    [Fact]
    public async Task SubmitAsync_UsesPaperBrokerWhenRuntimeAndPolicyApprove()
    {
        var broker = new FakeBroker();
        RuntimeModeState mode = ReadyMode();
        var service = CreateService(broker, mode);

        PaperOrderSubmission result = await service.SubmitAsync(
            new PaperOrderRequest("SPY", "buy", 1, 100, "qd-test-1"), CancellationToken.None);

        Assert.True(result.Accepted);
        Assert.Equal("broker-1", result.BrokerOrderId);
        Assert.NotNull(broker.Submitted);
        Assert.Equal(100, broker.Submitted.LimitPrice);
    }

    [Fact]
    public async Task SubmitAsync_RejectsBeforeBrokerWhenRuntimeIsNotReady()
    {
        var broker = new FakeBroker();
        var service = CreateService(broker, new RuntimeModeState());

        PaperOrderSubmission result = await service.SubmitAsync(
            new PaperOrderRequest("SPY", "buy", 1, 100, null), CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal("RUNTIME_NOT_READY", result.ReasonCode);
        Assert.Null(broker.Submitted);
    }

    [Fact]
    public async Task SubmitAsync_RejectsNotionalAndInvalidClientIdBeforeSubmission()
    {
        var broker = new FakeBroker();
        PaperOrderApplicationService service = CreateService(broker, ReadyMode());

        PaperOrderSubmission tooLarge = await service.SubmitAsync(
            new PaperOrderRequest("SPY", "buy", 11, 100, null), CancellationToken.None);
        PaperOrderSubmission invalidId = await service.SubmitAsync(
            new PaperOrderRequest("SPY", "buy", 1, 100, "contains spaces"), CancellationToken.None);

        Assert.Equal("ORDER_NOTIONAL_LIMIT", tooLarge.ReasonCode);
        Assert.Equal("INVALID_CLIENT_ORDER_ID", invalidId.ReasonCode);
        Assert.Null(broker.Submitted);
    }

    [Theory]
    [InlineData("sell", 1, true)]    // closes part of a 2-lot long
    [InlineData("sell", 2, true)]    // closes it exactly
    [InlineData("sell", 3, false)]   // would cross through flat into a short
    [InlineData("buy", 1, false)]    // adds to the long
    public async Task WhileEntryIsHaltedOnlyRiskReducingOrdersAreAccepted(
        string side, decimal quantity, bool expectedAccepted)
    {
        // The preflight enters EntryHalted the instant any position exists, so rejecting every order in
        // that mode locked an operator out of closing the very position that caused the halt.
        var broker = new FakeBroker { Positions = [new BrokerPositionSnapshot("SPY", 0, 2m, 500m)] };
        PaperOrderApplicationService service = CreateService(broker, HaltedMode());

        PaperOrderSubmission result = await service.SubmitAsync(
            new PaperOrderRequest("SPY", side, quantity, 500m, "op-1"), CancellationToken.None);

        Assert.Equal(expectedAccepted, result.Accepted);
        if (!expectedAccepted) Assert.Equal("ENTRY_HALTED", result.ReasonCode);
    }

    [Fact]
    public async Task AClosingOrderIsSubmittedWithClosingIntent()
    {
        // The intent was hardcoded to Open, so every operator close was journalled as an opening trade.
        var broker = new FakeBroker { Positions = [new BrokerPositionSnapshot("SPY", 0, 2m, 500m)] };
        PaperOrderApplicationService service = CreateService(broker, ReadyMode());

        await service.SubmitAsync(
            new PaperOrderRequest("SPY", "sell", 2m, 500m, "op-1"), CancellationToken.None);

        Assert.Equal(PositionIntent.Close, broker.Submitted!.PositionIntent);
    }

    [Fact]
    public async Task ClosingAShortCountsAsRiskReduction()
    {
        var broker = new FakeBroker { Positions = [new BrokerPositionSnapshot("SPY", 0, -2m, 500m)] };
        PaperOrderApplicationService service = CreateService(broker, HaltedMode());

        PaperOrderSubmission result = await service.SubmitAsync(
            new PaperOrderRequest("SPY", "buy", 2m, 500m, "op-1"), CancellationToken.None);

        Assert.True(result.Accepted, result.ReasonCode);
    }

    [Fact]
    public async Task AModeWhereBrokerTruthIsUnknownStillRefusesEverything()
    {
        // EntryHalted means "stop adding exposure". Degraded means "we do not know the state at all",
        // and sizing a close against unknown state is worse than not closing.
        var broker = new FakeBroker { Positions = [new BrokerPositionSnapshot("SPY", 0, 2m, 500m)] };
        var mode = new RuntimeModeState();
        mode.Transition(SystemMode.Degraded, "test_degraded");
        PaperOrderApplicationService service = CreateService(broker, mode);

        PaperOrderSubmission result = await service.SubmitAsync(
            new PaperOrderRequest("SPY", "sell", 2m, 500m, "op-1"), CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal("RUNTIME_NOT_READY", result.ReasonCode);
    }

    private static RuntimeModeState HaltedMode()
    {
        var mode = new RuntimeModeState();
        mode.Transition(SystemMode.EntryHalted, "test_halted");
        return mode;
    }

    /// <summary>
    /// Infrastructure green, research plane dark — the state the system actually runs in, since no
    /// strategy qualifies. Manual orders must still be admissible here.
    /// </summary>
    private static FullSystemReadinessState InfrastructureReady()
    {
        var readiness = new FullSystemReadinessState();
        readiness.RecordDeterministicRuntime(true, true, true, true, true);
        readiness.RecordBrokerPreflight(reconciled: true, portfolioKnown: true, paperEndpointVerified: true);
        return readiness;
    }

    private static PaperOrderApplicationService CreateService(
        FakeBroker broker, RuntimeModeState mode, FullSystemReadinessState? readiness = null)
    {
        var resolver = new DictionaryInstrumentSymbolResolver(new Dictionary<int, string> { [0] = "SPY" });
        return new PaperOrderApplicationService(
            broker,
            resolver,
            new PaperTradingOptions(1_000, new Dictionary<int, string> { [0] = "SPY" }),
            mode,
            readiness ?? InfrastructureReady(),
            new LiveRuntimeClock());
    }

    private static RuntimeModeState ReadyMode()
    {
        var mode = new RuntimeModeState();
        mode.Transition(SystemMode.Ready, "test_ready");
        return mode;
    }

    private sealed class FakeBroker : IBrokerExecutionGateway
    {
        public ExecutionCommand? Submitted { get; private set; }
        public IReadOnlyList<BrokerPositionSnapshot> Positions { get; set; } = [];

        public Task<IReadOnlyList<BrokerPositionSnapshot>> ListPositionsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Positions);

        public Task<BrokerAccountSnapshot?> GetAccountAsync(CancellationToken cancellationToken) =>
            Task.FromResult<BrokerAccountSnapshot?>(new("account", "ACTIVE", 100_000, 100_000, false, false));

        public Task<BrokerSubmitResult> SubmitAsync(ExecutionCommand command, CancellationToken cancellationToken)
        {
            Submitted = command;
            return Task.FromResult(new BrokerSubmitResult(BrokerSubmitState.Acknowledged, "broker-1", null, "request-1"));
        }

        public Task<BrokerOrderSnapshot?> FindByClientOrderIdAsync(string clientOrderId, CancellationToken cancellationToken) =>
            Task.FromResult<BrokerOrderSnapshot?>(null);
    }

    [Fact]
    public async Task AManualOrderIsAdmittedWhileTheResearchPlaneIsDark()
    {
        // The gap this closes. Requiring SystemMode.Ready meant requiring featuresReady and
        // expertsReady, which describe the research plane. Since no strategy qualifies, Ready is
        // unreachable, so the operator's manual path could never accept an order at all -- an escape
        // hatch welded shut by the state of an unrelated subsystem.
        var broker = new FakeBroker();
        var mode = new RuntimeModeState();
        mode.Transition(SystemMode.Syncing, "test_infra_only");

        PaperOrderSubmission result = await CreateService(broker, mode).SubmitAsync(
            new PaperOrderRequest("SPY", "buy", 1, 100, "op-1"), CancellationToken.None);

        Assert.True(result.Accepted, result.ReasonCode);
    }

    [Fact]
    public async Task LosingBrokerTruthRefusesTheOrderEvenInAPermissiveMode()
    {
        // Infrastructure readiness is a real gate, not a formality: without broker truth an order is
        // sized against state nobody knows.
        var readiness = new FullSystemReadinessState();
        readiness.RecordDeterministicRuntime(true, true, true, true, true);
        readiness.RecordBrokerPreflight(reconciled: false, portfolioKnown: false, paperEndpointVerified: false);
        var mode = new RuntimeModeState();
        mode.Transition(SystemMode.Syncing, "test_no_broker_truth");

        PaperOrderSubmission result = await CreateService(new FakeBroker(), mode, readiness).SubmitAsync(
            new PaperOrderRequest("SPY", "buy", 1, 100, "op-1"), CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal("INFRASTRUCTURE_NOT_READY", result.ReasonCode);
    }

    [Theory]
    [InlineData(SystemMode.Emergency)]
    [InlineData(SystemMode.Shutdown)]
    [InlineData(SystemMode.Degraded)]
    public async Task HardStopModesStillRefuseEverythingWithoutTouchingTheBroker(SystemMode stopped)
    {
        var broker = new FakeBroker();
        var mode = new RuntimeModeState();
        mode.Transition(stopped, "test_hard_stop");

        PaperOrderSubmission result = await CreateService(broker, mode).SubmitAsync(
            new PaperOrderRequest("SPY", "sell", 1, 100, "op-1"), CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal("RUNTIME_NOT_READY", result.ReasonCode);
        Assert.Null(broker.Submitted);
    }
}
