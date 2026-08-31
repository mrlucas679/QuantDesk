using QuantDesk.Api.PaperTrading;
using QuantDesk.Domain.Execution;
using QuantDesk.Runtime.Modes;

namespace QuantDesk.Api.Tests;

public sealed class PaperRuntimePreflightServiceTests
{
    [Fact]
    public async Task NonPaperGatewayCannotSetPaperVerification()
    {
        var readiness = new FullSystemReadinessState();
        var service = CreateService(new FakeBroker(false), readiness);

        await service.CheckOnceAsync(CancellationToken.None);

        Assert.False(readiness.Snapshot().PaperEndpointVerified);
        Assert.False(readiness.Snapshot().BrokerReconciled);
    }

    [Fact]
    public async Task UnexplainedPositionCannotSetBrokerReconciliation()
    {
        var readiness = new FullSystemReadinessState();
        var broker = new FakeBroker(true)
        {
            Positions = [new BrokerPositionSnapshot("BTC/USD", 1, 0.001m, 100_000m)]
        };
        var service = CreateService(broker, readiness);

        await service.CheckOnceAsync(CancellationToken.None);

        Assert.True(readiness.Snapshot().PaperEndpointVerified);
        Assert.False(readiness.Snapshot().BrokerReconciled);
    }

    [Fact]
    public async Task HealthyFlatPaperAccountSetsBrokerReadiness()
    {
        var readiness = new FullSystemReadinessState();
        var service = CreateService(new FakeBroker(true), readiness);

        await service.CheckOnceAsync(CancellationToken.None);

        Assert.True(readiness.Snapshot().PaperEndpointVerified);
        Assert.True(readiness.Snapshot().PortfolioKnown);
        Assert.True(readiness.Snapshot().BrokerReconciled);
    }

    private static PaperRuntimePreflightService CreateService(
        IBrokerExecutionGateway broker,
        FullSystemReadinessState readiness) => new(
            broker,
            new RuntimeModeState(),
            readiness,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PaperRuntimePreflightService>.Instance);

    private sealed class FakeBroker(bool paper) : IBrokerExecutionGateway
    {
        public bool IsPaperEnvironment => paper;
        public IReadOnlyList<BrokerOrderSnapshot> Orders { get; init; } = [];
        public IReadOnlyList<BrokerPositionSnapshot> Positions { get; init; } = [];

        public Task<BrokerAccountSnapshot?> GetAccountAsync(CancellationToken cancellationToken) =>
            Task.FromResult<BrokerAccountSnapshot?>(new(
                "paper-account", "ACTIVE", 100_000m, 100_000m, false, false));

        public Task<IReadOnlyList<BrokerOrderSnapshot>> ListOpenOrdersAsync(
            CancellationToken cancellationToken) => Task.FromResult(Orders);

        public Task<IReadOnlyList<BrokerPositionSnapshot>> ListPositionsAsync(
            CancellationToken cancellationToken) => Task.FromResult(Positions);

        public Task<BrokerSubmitResult> SubmitAsync(
            ExecutionCommand command,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<BrokerOrderSnapshot?> FindByClientOrderIdAsync(
            string clientOrderId,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
