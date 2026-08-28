using QuantDesk.Alpaca.Mapping;
using QuantDesk.Api.PaperTrading;
using QuantDesk.Domain.Execution;
using QuantDesk.Domain.Runtime;
using QuantDesk.Runtime.Modes;

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

    private static PaperOrderApplicationService CreateService(FakeBroker broker, RuntimeModeState mode)
    {
        var resolver = new DictionaryInstrumentSymbolResolver(new Dictionary<int, string> { [0] = "SPY" });
        return new PaperOrderApplicationService(
            broker,
            resolver,
            new PaperTradingOptions(1_000, new Dictionary<int, string> { [0] = "SPY" }),
            mode);
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
}
