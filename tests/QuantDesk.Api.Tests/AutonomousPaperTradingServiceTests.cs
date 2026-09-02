using Microsoft.Extensions.Logging.Abstractions;
using QuantDesk.Alpaca.Capabilities;
using QuantDesk.Alpaca.Mapping;
using QuantDesk.Alpaca.MarketData;
using QuantDesk.Api.PaperTrading;
using QuantDesk.Domain.Capabilities;
using QuantDesk.Domain.Execution;
using QuantDesk.Domain.Numerics;
using QuantDesk.Domain.Risk;
using QuantDesk.Runtime.Actionability;
using QuantDesk.Runtime.Costs;
using QuantDesk.Runtime.Execution;
using QuantDesk.Runtime.Experts;
using QuantDesk.Domain.Runtime;
using QuantDesk.Runtime.Modes;
using QuantDesk.Runtime.Options;
using QuantDesk.Runtime.Persistence;
using QuantDesk.Runtime.Positions;
using QuantDesk.Runtime.Risk;
using QuantDesk.Runtime.State;
using QuantDesk.Runtime.Strategies;
using QuantDesk.Runtime.Time;
using QuantDesk.Domain.Trading;

namespace QuantDesk.Api.Tests;

/// <summary>
/// Covers the autonomous evaluation cycle: the class that decides whether to trade, halts on
/// unreconciled broker state, and hands an approved view to execution. It had no tests despite
/// being the money path.
///
/// Every broker here is a double. Nothing contacts Alpaca and no order is placed.
/// </summary>
public sealed class AutonomousPaperTradingServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 15, 0, 0, TimeSpan.Zero);
    private readonly string _storePath = Path.Combine(
        Path.GetTempPath(), $"qd-auto-{Guid.NewGuid():N}.json");
    private readonly string _spotStorePath = Path.Combine(
        Path.GetTempPath(), $"qd-auto-spot-{Guid.NewGuid():N}.json");
    private readonly string _diagnosticStorePath = Path.Combine(
        Path.GetTempPath(), $"qd-auto-diag-{Guid.NewGuid():N}.json");

    [Fact]
    public async Task AnUnroutableSymbolAbstainsAndNeverContactsTheBroker()
    {
        var broker = new FakeBroker();
        (AutonomousPaperTradingService service, AutonomousTradingState state) =
            Build(broker, symbol: "NOT-A-SYMBOL");

        await service.EvaluateOpportunityAsync(CancellationToken.None);

        Assert.Equal("abstained", state.Snapshot().State);
        Assert.Equal(0, broker.AccountReads);
        Assert.Empty(broker.Submitted);
    }

    [Fact]
    public async Task AnAccountWithoutPermissionForTheAssetClassAbstains()
    {
        var broker = new FakeBroker();
        (AutonomousPaperTradingService service, AutonomousTradingState state) =
            Build(broker, symbol: "SPY", capabilities: Capabilities(equity: false));

        await service.EvaluateOpportunityAsync(CancellationToken.None);

        Assert.Equal("abstained", state.Snapshot().State);
        Assert.Equal("AssetClassNotPermitted", state.Snapshot().Reason);
        Assert.Empty(broker.Submitted);
    }

    [Fact]
    public async Task AnExistingBrokerPositionHaltsEntryAndDegradesTheRuntime()
    {
        var broker = new FakeBroker
        {
            Positions = [new BrokerPositionSnapshot("BTC/USD", 0, 1m, 100m)]
        };
        (AutonomousPaperTradingService service, AutonomousTradingState state, RuntimeModeState mode) =
            BuildWithMode(broker);

        await service.EvaluateOpportunityAsync(CancellationToken.None);

        Assert.Equal("entry_halted", state.Snapshot().State);
        Assert.Equal("PortfolioUnreconciled", state.Snapshot().Reason);
        Assert.Equal(SystemMode.EntryHalted, mode.Snapshot().Mode);
        Assert.Empty(broker.Submitted);
    }

    [Fact]
    public async Task AnExistingOpenOrderHaltsEntry()
    {
        var broker = new FakeBroker
        {
            OpenOrders = [new BrokerOrderSnapshot("broker-stale", "stale", "new", 0m, null)]
        };
        (AutonomousPaperTradingService service, AutonomousTradingState state) = Build(broker);

        await service.EvaluateOpportunityAsync(CancellationToken.None);

        Assert.Equal("entry_halted", state.Snapshot().State);
        Assert.Empty(broker.Submitted);
    }

    [Fact]
    public async Task AWeakSignalAbstainsWithTheGateReasonAndPlacesNoOrder()
    {
        // A 0.3% drift over the evidence window cannot clear the 50 bps crypto venue fee.
        var broker = new FakeBroker();
        (AutonomousPaperTradingService service, AutonomousTradingState state) =
            Build(broker, evidence: Evidence(100m, 100.01m, 100m, 100.3m));

        await service.EvaluateOpportunityAsync(CancellationToken.None);

        Assert.Equal("abstained", state.Snapshot().State);
        Assert.Equal("EXPECTED_EDGE_BELOW_COSTS", state.Snapshot().Reason);
        Assert.Empty(broker.Submitted);
    }

    [Fact]
    public async Task AnOrderBelowTheVenueMinimumIsRefusedBeforeAnythingIsReserved()
    {
        // A strong signal, but five dollars of BTC is below what the venue will accept and rounds
        // toward dust. This is the case that motivated the guard: never pay the broker to take a
        // position too small to be worth holding.
        var broker = new FakeBroker();
        (AutonomousPaperTradingService service, AutonomousTradingState state) = Build(
            broker, symbol: "BTC/USD", notional: 5m, evidence: Evidence(100m, 100.01m, 100m, 104m));

        await service.EvaluateOpportunityAsync(CancellationToken.None);

        Assert.Equal("abstained", state.Snapshot().State);
        Assert.Equal("NotionalBelowVenueMinimum", state.Snapshot().Reason);
        Assert.Empty(broker.Submitted);
    }

    [Fact]
    public async Task AnApprovedSpotViewIsHandedToTheDurableLifecycleNotHeldInMemory()
    {
        var broker = new FakeBroker();
        (AutonomousPaperTradingService service, AutonomousTradingState state) =
            Build(broker, evidence: Evidence(100m, 100.01m, 100m, 104m));

        await service.EvaluateOpportunityAsync(CancellationToken.None);

        // The record must exist on disk before the cycle returns, so a restart can resume it.
        Assert.True(File.Exists(_spotStorePath));
        SpotExecutionRecord record = Assert.Single(new SpotExecutionStore(_spotStorePath).ListAll());
        Assert.Equal("BTC/USD", record.Symbol);
        Assert.True(record.Quantity > 0);
        Assert.StartsWith("qd-spot-", record.EntryClientOrderId, StringComparison.Ordinal);
        Assert.Equal(1, broker.SubmitCount);
        // The cycle hands off rather than blocking on the fill.
        Assert.Equal("holding", state.Snapshot().State);
    }

    [Fact]
    public async Task TheSameOpportunityNeverProducesASecondDurableRecord()
    {
        var broker = new FakeBroker();
        (AutonomousPaperTradingService service, _) =
            Build(broker, evidence: Evidence(100m, 100.01m, 100m, 104m));

        await service.EvaluateOpportunityAsync(CancellationToken.None);
        await service.EvaluateOpportunityAsync(CancellationToken.None);

        // Identity is derived from the opportunity, so a repeat cycle cannot double-submit.
        Assert.Single(new SpotExecutionStore(_spotStorePath).ListAll());
        Assert.Equal(1, broker.SubmitCount);
    }

    [Fact]
    public async Task AnUnhealthyAccountStopsBeforeAnyDecision()
    {
        var broker = new FakeBroker { AccountBlocked = true };
        (AutonomousPaperTradingService service, _) = Build(broker);

        await Assert.ThrowsAnyAsync<Exception>(
            () => service.EvaluateOpportunityAsync(CancellationToken.None));
        Assert.Empty(broker.Submitted);
    }

    [Fact]
    public async Task ADisabledServiceReportsDisabledAndDoesNothing()
    {
        var broker = new FakeBroker();
        (AutonomousPaperTradingService service, AutonomousTradingState state) =
            Build(broker, enabled: false);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal("disabled", state.Snapshot().State);
        Assert.Equal(0, broker.AccountReads);
        Assert.Empty(broker.Submitted);
    }

    private (AutonomousPaperTradingService, AutonomousTradingState) Build(
        FakeBroker broker,
        string symbol = "BTC/USD",
        decimal notional = 500m,
        bool enabled = true,
        CapabilityReport? capabilities = null,
        DirectionalMarketEvidence? evidence = null)
    {
        (AutonomousPaperTradingService service, AutonomousTradingState state, _) =
            BuildWithMode(broker, symbol, notional, enabled, capabilities, evidence);
        return (service, state);
    }

    private (AutonomousPaperTradingService, AutonomousTradingState, RuntimeModeState) BuildWithMode(
        FakeBroker broker,
        string symbol = "BTC/USD",
        decimal notional = 500m,
        bool enabled = true,
        CapabilityReport? capabilities = null,
        DirectionalMarketEvidence? evidence = null)
    {
        var clock = new VirtualRuntimeClock(Now);
        var mode = new RuntimeModeState();
        mode.Transition(SystemMode.Ready, "test");
        var state = new AutonomousTradingState();
        var resolver = new DictionaryInstrumentSymbolResolver(
            new Dictionary<int, string> { [0] = "BTC/USD", [1] = "SPY" });
        var options = new AutonomousPaperTradingOptions(
            enabled, AutonomousTradingMode.ExperimentalPaper, OpportunityExpression.Spot,
            null, symbol, notional, TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(5));

        var pipeline = new AutonomousDecisionPipeline(
            new MarketStateStore(2), new ExpertCommittee(0.6, 1),
            new CryptoDirectionalStrategyCompiler(
                new Usd(notional), 0.05, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)),
            new CryptoResearchGate(), new CryptoCostModel(new BasisPoints(50), new BasisPoints(10)),
            new ActionabilityGate(0.01, new Usd(0.01m)),
            new RiskGovernor(RiskLimitOptions.FromEnvironment(notional)),
            clock, NullLogger<AutonomousDecisionPipeline>.Instance);

        var lifecycle = new MultiLegExecutionLifecycle(
            broker, broker, new MultiLegExecutionStore(_storePath), clock, TimeSpan.FromSeconds(30));
        var coordinator = new OptionExecutionCoordinator(
            null!, lifecycle, NullLogger<OptionExecutionCoordinator>.Instance);
        var spotLifecycle = new SpotExecutionLifecycle(
            broker, new SpotExecutionStore(_spotStorePath), clock, TimeSpan.FromSeconds(30));

        // The lanes' own stores supply the claims, so exposure these tests create through the spot and
        // multi-leg lifecycles is attributed exactly as it would be in production.
        var attributor = new BrokerExposureAttributor(
        [
            new SpotExposureClaimSource(new SpotExecutionStore(_spotStorePath)),
            new MultiLegExposureClaimSource(new MultiLegExecutionStore(_storePath))
        ]);

        var service = new AutonomousPaperTradingService(
            broker, resolver,
            new DiagnosticStoreRealisedCostSource(
                new DiagnosticExecutionStore(_diagnosticStorePath),
                new SpotExecutionStore(_spotStorePath)),
            new StubEvidenceProvider(evidence ?? Evidence(100m, 100.01m, 100m, 104m)),
            attributor,
            new OpportunityRouter(), coordinator, spotLifecycle,
            new StubCapabilityProbe(capabilities ?? Capabilities()),
            pipeline, new ResearchArtifactState(),
            options, mode, state, clock,
            NullLogger<AutonomousPaperTradingService>.Instance);
        return (service, state, mode);
    }

    private static CapabilityReport Capabilities(bool equity = true, bool options = true) =>
        new(true, equity, true, options, options ? 3 : null, true, false,
            "iex", "indicative", null, null, [], null);

    private static DirectionalMarketEvidence Evidence(
        decimal bid, decimal ask, decimal first, decimal last)
    {
        decimal step = (last - first) / 12m;
        return new DirectionalMarketEvidence(
            bid, ask, [.. Enumerable.Range(0, 13).Select(index => first + step * index)]);
    }

    public void Dispose()
    {
        if (File.Exists(_storePath)) File.Delete(_storePath);
        if (File.Exists(_spotStorePath)) File.Delete(_spotStorePath);
    }

    private sealed class StubCapabilityProbe(CapabilityReport report) : IAlpacaCapabilityProbe
    {
        public Task<CapabilityReport> ProbeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(report);
    }

    private sealed class StubEvidenceProvider(DirectionalMarketEvidence evidence)
        : IMarketEvidenceProvider
    {
        public Task<DirectionalMarketEvidence> GetEvidenceAsync(
            OpportunityRoute route, CancellationToken cancellationToken) =>
            Task.FromResult(evidence);
    }

    private sealed class FakeBroker : IBrokerExecutionGateway, IMultiLegBrokerExecutionGateway
    {
        public int SubmitCount => Submitted.Count;
        public List<ExecutionCommand> Submitted { get; } = [];
        public IReadOnlyList<BrokerOrderSnapshot> OpenOrders { get; init; } = [];
        public IReadOnlyList<BrokerPositionSnapshot> Positions { get; init; } = [];
        public bool AccountBlocked { get; init; }
        public int AccountReads { get; private set; }

        public bool IsPaperEnvironment => true;

        public Task<BrokerAccountSnapshot?> GetAccountAsync(CancellationToken cancellationToken)
        {
            AccountReads++;
            return Task.FromResult<BrokerAccountSnapshot?>(new BrokerAccountSnapshot(
                "test-account", "ACTIVE", 100_000m, 100_000m,
                TradingBlocked: AccountBlocked, AccountBlocked: AccountBlocked)
            {
                CryptoTradingStatus = "ACTIVE"
            });
        }

        public Task<BrokerSubmitResult> SubmitAsync(
            ExecutionCommand command, CancellationToken cancellationToken)
        {
            Submitted.Add(command);
            return Task.FromResult(new BrokerSubmitResult(
                BrokerSubmitState.Acknowledged, "broker-1", null, null));
        }

        public Task<BrokerSubmitResult> SubmitMultiLegAsync(
            MultiLegExecutionCommand command, CancellationToken cancellationToken) =>
            Task.FromResult(new BrokerSubmitResult(
                BrokerSubmitState.Acknowledged, "broker-mleg", null, null));

        public Task<BrokerOrderSnapshot?> FindByClientOrderIdAsync(
            string clientOrderId, CancellationToken cancellationToken) =>
            Task.FromResult<BrokerOrderSnapshot?>(null);

        public Task<IReadOnlyList<BrokerOrderSnapshot>> ListOpenOrdersAsync(
            CancellationToken cancellationToken) => Task.FromResult(OpenOrders);

        public Task<IReadOnlyList<BrokerPositionSnapshot>> ListPositionsAsync(
            CancellationToken cancellationToken) => Task.FromResult(Positions);
    }
}
