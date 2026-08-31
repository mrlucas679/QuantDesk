using Microsoft.Extensions.Logging.Abstractions;
using QuantDesk.Api.PaperTrading;
using QuantDesk.Domain.Execution;
using QuantDesk.Domain.Trading;
using QuantDesk.Runtime.Execution;
using QuantDesk.Runtime.Persistence;
using QuantDesk.Runtime.Time;

namespace QuantDesk.Api.Tests;

public sealed class MultiLegExecutionRecoveryServiceTests
{
    [Fact]
    public async Task HostedRecoveryAdvancesSeededFilledRecordWithoutSubmittingAnOrder()
    {
        string path = Path.Combine(Path.GetTempPath(), $"qd-mleg-recovery-{Guid.NewGuid():N}.json");
        try
        {
            var broker = new NoSubmitBroker();
            var store = new MultiLegExecutionStore(path);
            DateTimeOffset now = new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);
            MultiLegExecutionCommand entry = Command("entry", PositionIntent.BuyToOpen, PositionIntent.SellToOpen);
            MultiLegExecutionCommand exit = Command("exit", PositionIntent.SellToClose, PositionIntent.BuyToClose);
            Assert.True(store.TryCreate(new MultiLegExecutionRecord(
                "RECOVERY-SEEDED", "spy-vertical", MultiLegExecutionState.EntryFilled, entry, exit, now, now)
            {
                MaximumHoldingPeriod = TimeSpan.FromHours(1),
                EntryFinalFillAt = now,
                ScheduledExitAt = now.AddHours(1)
            }));
            var lifecycle = new MultiLegExecutionLifecycle(
                broker, broker, store, new VirtualRuntimeClock(now), TimeSpan.FromSeconds(1));
            var service = new MultiLegExecutionRecoveryService(lifecycle,
                NullLogger<MultiLegExecutionRecoveryService>.Instance);

            await service.StartAsync(CancellationToken.None);
            for (int attempt = 0; attempt < 20 && service.LastCycleAt is null; attempt++)
                await Task.Delay(25);
            await service.StopAsync(CancellationToken.None);

            Assert.NotNull(service.StartedAt);
            Assert.NotNull(service.LastCycleAt);
            Assert.Null(service.LastError);
            Assert.Equal(MultiLegExecutionState.Holding, store.Find("RECOVERY-SEEDED")!.State);
            Assert.Equal(0, broker.MultiLegSubmitCalls);
            Assert.Equal(0, broker.SingleLegSubmitCalls);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
        }
    }

    private static MultiLegExecutionCommand Command(string id, PositionIntent first, PositionIntent second) => new(
        id, 1, ExecutionOrderType.Limit, ExecutionTimeInForce.Day, 1m,
        [
            new MultiLegExecutionLeg("SPY260904C00650000", 1,
                first is PositionIntent.BuyToOpen or PositionIntent.BuyToClose ? OrderSide.Buy : OrderSide.Sell, first),
            new MultiLegExecutionLeg("SPY260904C00655000", 1,
                second is PositionIntent.BuyToOpen or PositionIntent.BuyToClose ? OrderSide.Buy : OrderSide.Sell, second)
        ]);

    private sealed class NoSubmitBroker : IMultiLegBrokerExecutionGateway, IBrokerExecutionGateway
    {
        public bool IsPaperEnvironment => true;
        public int MultiLegSubmitCalls { get; private set; }
        public int SingleLegSubmitCalls { get; private set; }
        public Task<BrokerSubmitResult> SubmitMultiLegAsync(MultiLegExecutionCommand command, CancellationToken cancellationToken)
        {
            MultiLegSubmitCalls++;
            throw new InvalidOperationException("Recovery must not submit a seeded filled record.");
        }
        public Task<BrokerSubmitResult> SubmitAsync(ExecutionCommand command, CancellationToken cancellationToken)
        {
            SingleLegSubmitCalls++;
            throw new InvalidOperationException("Recovery must not submit a seeded filled record.");
        }
        public Task<BrokerOrderSnapshot?> FindByClientOrderIdAsync(string clientOrderId, CancellationToken cancellationToken) =>
            Task.FromResult<BrokerOrderSnapshot?>(null);
    }
}
