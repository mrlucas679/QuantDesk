using QuantDesk.Alpaca.Mapping;
using QuantDesk.Api.PaperTrading;
using QuantDesk.Domain.Contracts;
using QuantDesk.Domain.Execution;
using QuantDesk.Domain.Numerics;
using QuantDesk.Domain.Options;
using QuantDesk.Domain.Risk;
using QuantDesk.Domain.Strategies;
using QuantDesk.Domain.Trading;
using QuantDesk.Runtime.Execution;
using QuantDesk.Runtime.Persistence;
using QuantDesk.Runtime.Time;

namespace QuantDesk.Api.Tests;

public sealed class DefinedRiskVerticalLifecycleServiceTests
{
    [Fact]
    public void ReservesExactArtifactBoundVerticalBeforeAnyBrokerPost()
    {
        using var fixture = new Fixture();
        MultiLegOptionCandidate candidate = fixture.Candidate();

        bool reserved = fixture.Service.TryReserve(
            "OPTION-ARTIFACT-1", candidate, Definition(), ApprovedRisk(20m));

        MultiLegExecutionRecord record = fixture.Store.Find("OPTION-ARTIFACT-1")!;
        Assert.True(reserved);
        Assert.Equal(MultiLegExecutionState.EntryReserved, record.State);
        Assert.Equal(20m, record.DefinedMaximumLoss);
        Assert.Equal("SPY260918C00600000", record.EntryCommand.Legs[0].Symbol);
        Assert.Equal(PositionIntent.BuyToOpen, record.EntryCommand.Legs[0].PositionIntent);
        Assert.Equal(PositionIntent.SellToOpen, record.EntryCommand.Legs[1].PositionIntent);
        Assert.Equal(0, fixture.Broker.SubmitCalls);
    }

    [Fact]
    public void RejectsASelectionWhoseMaximumLossExceedsApprovedRisk()
    {
        using var fixture = new Fixture();

        bool reserved = fixture.Service.TryReserve(
            "OPTION-ARTIFACT-2", fixture.Candidate(), Definition(), ApprovedRisk(10m));

        Assert.False(reserved);
        Assert.Null(fixture.Store.Find("OPTION-ARTIFACT-2"));
    }

    private static RiskDecision ApprovedRisk(decimal requiredRisk) => new(
        true, RiskReason.Approved, new Usd(requiredRisk), new Usd(requiredRisk));

    private static StrategyDefinitionContract Definition() => new(
        "SPY", 5, 60, "spy-vertical-v1", "State", "{}",
        new ExitPolicyDefinitionContract("vertical-exit-v1", 60, true, true))
    {
        ExecutionKind = StrategyExecutionKind.DefinedRiskVertical,
        OptionVertical = new OptionVerticalExecutionPolicyContract(7, 60, .05m, 20m, .5m)
    };

    private sealed class Fixture : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"qd-option-{Guid.NewGuid():N}.json");
        private readonly DictionaryInstrumentSymbolResolver _symbols = new(new Dictionary<int, string> { [0] = "SPY" });
        public FakeBroker Broker { get; } = new();
        public MultiLegExecutionStore Store { get; }
        public DefinedRiskVerticalLifecycleService Service { get; }

        public Fixture()
        {
            Store = new MultiLegExecutionStore(_path);
            var lifecycle = new MultiLegExecutionLifecycle(
                Broker, Broker, Store, new VirtualRuntimeClock(DateTimeOffset.UtcNow), TimeSpan.FromSeconds(30));
            _symbols.TryRegisterOptionSymbol("SPY260918C00600000", out _);
            _symbols.TryRegisterOptionSymbol("SPY260918C00605000", out _);
            Service = new DefinedRiskVerticalLifecycleService(lifecycle, _symbols);
        }

        public MultiLegOptionCandidate Candidate()
        {
            _symbols.TryResolveBySymbol("SPY260918C00600000", out int longSlot);
            _symbols.TryResolveBySymbol("SPY260918C00605000", out int shortSlot);
            return new MultiLegOptionCandidate(1, "spy-bull-call-vertical-v1",
                [new OptionLegCandidate(longSlot, OrderSide.Buy, PositionIntent.Open, 1),
                 new OptionLegCandidate(shortSlot, OrderSide.Sell, PositionIntent.Open, 1)],
                1.25m, new Usd(20m),
                new PositionManagementPlan(TimeSpan.FromMinutes(60), true, true, new Usd(20m), 7, "vertical-exit-v1"));
        }

        public void Dispose()
        {
            if (File.Exists(_path)) File.Delete(_path);
            if (File.Exists(_path + ".tmp")) File.Delete(_path + ".tmp");
        }
    }

    private sealed class FakeBroker : IMultiLegBrokerExecutionGateway, IBrokerExecutionGateway
    {
        public bool IsPaperEnvironment => true;
        public int SubmitCalls { get; private set; }
        public Task<BrokerSubmitResult> SubmitMultiLegAsync(MultiLegExecutionCommand command, CancellationToken cancellationToken)
        {
            SubmitCalls++;
            return Task.FromResult(new BrokerSubmitResult(BrokerSubmitState.Acknowledged, "unused", null, null));
        }
        public Task<BrokerOrderSnapshot?> FindByClientOrderIdAsync(string clientOrderId, CancellationToken cancellationToken) =>
            Task.FromResult<BrokerOrderSnapshot?>(null);
        public Task<BrokerSubmitResult> SubmitAsync(ExecutionCommand command, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
