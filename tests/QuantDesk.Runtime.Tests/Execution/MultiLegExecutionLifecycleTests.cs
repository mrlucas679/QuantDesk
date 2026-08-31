using QuantDesk.Domain.Execution;
using QuantDesk.Domain.Trading;
using QuantDesk.Runtime.Execution;
using QuantDesk.Runtime.Persistence;
using QuantDesk.Runtime.Time;

namespace QuantDesk.Runtime.Tests.Execution;

public sealed class MultiLegExecutionLifecycleTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 31, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ReservationIsDurableBeforeOneAndOnlyOneEntryPost()
    {
        using var fixture = CreateFixture();
        Assert.True(fixture.Lifecycle.TryReserve(
            "OPTIONS-1", "spy-vertical", 1, 1.25m, 1.50m, 125m,
            TimeSpan.FromHours(1), EntryLegs()));
        fixture.Broker.BeforeSubmit = command =>
        {
            MultiLegExecutionRecord persisted = fixture.Store.Find("OPTIONS-1")!;
            Assert.Equal(MultiLegExecutionState.EntryReserved, persisted.State);
            Assert.NotNull(persisted.EntrySubmissionAttemptedAt);
            Assert.Equal(command.ClientOrderId, persisted.EntryCommand.ClientOrderId);
        };

        await fixture.Lifecycle.AdvanceAsync("OPTIONS-1", CancellationToken.None);
        await fixture.Lifecycle.AdvanceAsync("OPTIONS-1", CancellationToken.None);

        Assert.Equal(1, fixture.Broker.SubmitCalls);
        Assert.Equal(MultiLegExecutionState.EntryAccepted, fixture.Store.Find("OPTIONS-1")!.State);
    }

    [Fact]
    public async Task TimeoutAfterAcceptanceRecoversSameDeterministicOrderAcrossRestart()
    {
        using var fixture = CreateFixture();
        Assert.True(fixture.Lifecycle.TryReserve(
            "OPTIONS-RECOVERY", "spy-vertical", 1, 1.25m, 1.50m, 125m,
            TimeSpan.FromHours(1), EntryLegs()));
        fixture.Broker.ThrowAfterAcceptance = true;

        MultiLegExecutionRecord recovered = await fixture.Lifecycle.AdvanceAsync(
            "OPTIONS-RECOVERY", CancellationToken.None);
        var restarted = new MultiLegExecutionLifecycle(
            fixture.Broker, fixture.Broker, new MultiLegExecutionStore(fixture.Path), fixture.Clock,
            TimeSpan.FromSeconds(1));
        await restarted.AdvanceAsync("OPTIONS-RECOVERY", CancellationToken.None);

        Assert.Equal(1, fixture.Broker.SubmitCalls);
        Assert.Equal(MultiLegExecutionState.EntryAccepted, recovered.State);
        Assert.Equal("broker-1", recovered.EntryBrokerOrderId);
        Assert.Equal(MultiLegExecutionLifecycle.DeterministicClientOrderId("OPTIONS-RECOVERY", "entry"),
            recovered.EntryCommand.ClientOrderId);
    }

    [Fact]
    public async Task AmbiguousResultRecoversAcceptedOrderBeforeAnyRetry()
    {
        using var fixture = CreateFixture();
        Assert.True(fixture.Lifecycle.TryReserve(
            "OPTIONS-AMBIGUOUS", "spy-vertical", 1, 1.25m, 1.50m, 125m,
            TimeSpan.FromHours(1), EntryLegs()));
        fixture.Broker.ReturnUnknownAfterAcceptance = true;

        MultiLegExecutionRecord recovered = await fixture.Lifecycle.AdvanceAsync(
            "OPTIONS-AMBIGUOUS", CancellationToken.None);
        await fixture.Lifecycle.AdvanceAsync("OPTIONS-AMBIGUOUS", CancellationToken.None);

        Assert.Equal(1, fixture.Broker.SubmitCalls);
        Assert.Equal(MultiLegExecutionState.EntryAccepted, recovered.State);
        Assert.Equal("broker-1", recovered.EntryBrokerOrderId);
    }

    [Fact]
    public async Task AcceptedPartialFilledHoldExitAndZeroReconciliationPersist()
    {
        using var fixture = CreateFixture();
        Assert.True(fixture.Lifecycle.TryReserve(
            "OPTIONS-FULL", "spy-vertical", 1, 1.25m, 1.50m, 125m,
            TimeSpan.FromMinutes(30), EntryLegs()));
        await fixture.Lifecycle.AdvanceAsync("OPTIONS-FULL", CancellationToken.None);
        string entryId = fixture.Store.Find("OPTIONS-FULL")!.EntryCommand.ClientOrderId;

        fixture.Broker.Orders[entryId] = Snapshot("broker-1", entryId, "partially_filled", 0.5m, 1.20m);
        MultiLegExecutionRecord partial = await fixture.Lifecycle.AdvanceAsync("OPTIONS-FULL", CancellationToken.None);
        Assert.Equal(MultiLegExecutionState.EntryPartiallyFilled, partial.State);
        Assert.Equal(0.5m, partial.EntryFilledQuantity);

        fixture.Broker.Orders[entryId] = Snapshot("broker-1", entryId, "filled", 1m, 1.25m) with
        {
            FilledAt = Start.AddMinutes(1)
        };
        MultiLegExecutionRecord filled = await fixture.Lifecycle.AdvanceAsync("OPTIONS-FULL", CancellationToken.None);
        Assert.Equal(MultiLegExecutionState.EntryFilled, filled.State);
        Assert.Equal(Start.AddMinutes(31), filled.ScheduledExitAt);
        await fixture.Lifecycle.AdvanceAsync("OPTIONS-FULL", CancellationToken.None);
        fixture.Clock.Advance(TimeSpan.FromMinutes(31));
        await fixture.Lifecycle.AdvanceAsync("OPTIONS-FULL", CancellationToken.None);
        await fixture.Lifecycle.AdvanceAsync("OPTIONS-FULL", CancellationToken.None);
        await fixture.Lifecycle.AdvanceAsync("OPTIONS-FULL", CancellationToken.None);
        string exitId = fixture.Store.Find("OPTIONS-FULL")!.ExitCommand.ClientOrderId;
        fixture.Broker.Orders[exitId] = Snapshot("broker-2", exitId, "partially_filled", 0.25m, 1.40m);
        Assert.Equal(MultiLegExecutionState.ExitPartiallyFilled,
            (await fixture.Lifecycle.AdvanceAsync("OPTIONS-FULL", CancellationToken.None)).State);
        fixture.Broker.Orders[exitId] = Snapshot("broker-2", exitId, "filled", 1m, 1.50m);
        Assert.Equal(MultiLegExecutionState.ExitFilled,
            (await fixture.Lifecycle.AdvanceAsync("OPTIONS-FULL", CancellationToken.None)).State);
        await fixture.Lifecycle.AdvanceAsync("OPTIONS-FULL", CancellationToken.None);
        MultiLegExecutionRecord complete = await fixture.Lifecycle.AdvanceAsync(
            "OPTIONS-FULL", CancellationToken.None);

        Assert.Equal(2, fixture.Broker.SubmitCalls);
        Assert.Equal(MultiLegExecutionState.Complete, complete.State);
        Assert.Equal(0m, complete.InternalOpenQuantity);
        Assert.NotNull(complete.CompletedAt);
    }

    [Fact]
    public void StoreReconstructionPreservesCommandsHoldPolicyAndDuplicateFence()
    {
        using var fixture = CreateFixture();
        Assert.True(fixture.Lifecycle.TryReserve(
            "OPTIONS-STORE", "spy-vertical", 1, 1.25m, 1.50m, 125m,
            TimeSpan.FromHours(2), EntryLegs()));
        var restarted = new MultiLegExecutionStore(fixture.Path);
        MultiLegExecutionRecord record = restarted.Find("OPTIONS-STORE")!;

        Assert.Equal(TimeSpan.FromHours(2), record.MaximumHoldingPeriod);
        Assert.Equal(MultiLegExecutionLifecycle.DeterministicClientOrderId("OPTIONS-STORE", "entry"),
            record.EntryCommand.ClientOrderId);
        Assert.False(restarted.TryCreate(record with { ExecutionId = "OPTIONS-DUPLICATE" }));
    }

    private static Fixture CreateFixture()
    {
        string path = Path.Combine(Path.GetTempPath(), $"qd-mleg-{Guid.NewGuid():N}.json");
        var broker = new FakeBroker();
        var clock = new VirtualRuntimeClock(Start);
        var store = new MultiLegExecutionStore(path);
        return new Fixture(path, broker, clock, store,
            new MultiLegExecutionLifecycle(broker, broker, store, clock, TimeSpan.FromSeconds(1)));
    }

    private static IReadOnlyList<MultiLegExecutionLeg> EntryLegs() =>
    [
        new("SPY260904C00650000", 1, OrderSide.Buy, PositionIntent.BuyToOpen),
        new("SPY260904C00655000", 1, OrderSide.Sell, PositionIntent.SellToOpen)
    ];

    private static BrokerOrderSnapshot Snapshot(
        string brokerId, string clientId, string status, decimal quantity, decimal price) =>
        new(brokerId, clientId, status, quantity, price) { SubmittedAt = Start };

    private sealed record Fixture(
        string Path,
        FakeBroker Broker,
        VirtualRuntimeClock Clock,
        MultiLegExecutionStore Store,
        MultiLegExecutionLifecycle Lifecycle) : IDisposable
    {
        public void Dispose()
        {
            if (File.Exists(Path)) File.Delete(Path);
            if (File.Exists(Path + ".tmp")) File.Delete(Path + ".tmp");
        }
    }

    private sealed class FakeBroker : IMultiLegBrokerExecutionGateway, IBrokerExecutionGateway
    {
        public bool IsPaperEnvironment => true;
        public int SubmitCalls { get; private set; }
        public bool ThrowAfterAcceptance { get; set; }
        public bool ReturnUnknownAfterAcceptance { get; set; }
        public Action<MultiLegExecutionCommand>? BeforeSubmit { get; set; }
        public Dictionary<string, BrokerOrderSnapshot> Orders { get; } = [];

        public Task<BrokerSubmitResult> SubmitMultiLegAsync(
            MultiLegExecutionCommand command,
            CancellationToken cancellationToken)
        {
            SubmitCalls++;
            BeforeSubmit?.Invoke(command);
            string brokerId = $"broker-{SubmitCalls}";
            Orders[command.ClientOrderId] = Snapshot(
                brokerId, command.ClientOrderId, "accepted", 0, 0);
            if (ThrowAfterAcceptance)
            {
                ThrowAfterAcceptance = false;
                throw new HttpRequestException("simulated response loss");
            }
            if (ReturnUnknownAfterAcceptance)
            {
                ReturnUnknownAfterAcceptance = false;
                return Task.FromResult(new BrokerSubmitResult(
                    BrokerSubmitState.Unknown, null, "BROKER_RESPONSE_INVALID", $"request-{SubmitCalls}"));
            }
            return Task.FromResult(new BrokerSubmitResult(
                BrokerSubmitState.Acknowledged, brokerId, null, $"request-{SubmitCalls}"));
        }

        public Task<BrokerOrderSnapshot?> FindByClientOrderIdAsync(
            string clientOrderId,
            CancellationToken cancellationToken) => Task.FromResult(
                Orders.GetValueOrDefault(clientOrderId));

        public Task<IReadOnlyList<BrokerOrderSnapshot>> ListOpenOrdersAsync(
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<BrokerOrderSnapshot>>(
                Orders.Values.Where(order => order.Status is "accepted" or "partially_filled").ToArray());

        public Task<IReadOnlyList<BrokerPositionSnapshot>> ListPositionsAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<BrokerPositionSnapshot>>([]);

        public Task<BrokerSubmitResult> SubmitAsync(
            ExecutionCommand command,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
