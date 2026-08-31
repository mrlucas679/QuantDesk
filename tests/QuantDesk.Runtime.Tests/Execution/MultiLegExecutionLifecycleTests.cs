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
    public async Task AmbiguousSubmissionThatNeverAppearsStopsWithoutASecondPost()
    {
        using var fixture = CreateFixture();
        Assert.True(fixture.Lifecycle.TryReserve(
            "OPTIONS-NOT-FOUND", "spy-vertical", 1, 1.25m, 1.50m, 125m,
            TimeSpan.FromHours(1), EntryLegs()));
        fixture.Broker.ReturnUnknownWithoutOrder = true;

        MultiLegExecutionRecord unknown = await fixture.Lifecycle.AdvanceAsync(
            "OPTIONS-NOT-FOUND", CancellationToken.None);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        MultiLegExecutionRecord unresolved = await fixture.Lifecycle.AdvanceAsync(
            "OPTIONS-NOT-FOUND", CancellationToken.None);

        Assert.Equal(MultiLegExecutionState.SubmissionUnknown, unknown.State);
        Assert.Equal(MultiLegExecutionState.SubmissionUnresolved, unresolved.State);
        Assert.Equal("ENTRY_SUBMISSION_LOOKUP_TIMEOUT", unresolved.FailureReason);
        Assert.Equal(1, fixture.Broker.SubmitCalls);
        Assert.Empty(fixture.Store.ListNonterminal());
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
    public async Task BrokenNestedLegFillRatioFailsClosed()
    {
        using var fixture = CreateFixture();
        Assert.True(fixture.Lifecycle.TryReserve(
            "OPTIONS-BROKEN-LEGS", "spy-vertical", 1, 1.25m, 1.50m, 125m,
            TimeSpan.FromHours(1), EntryLegs()));
        await fixture.Lifecycle.AdvanceAsync("OPTIONS-BROKEN-LEGS", CancellationToken.None);
        string entryId = fixture.Store.Find("OPTIONS-BROKEN-LEGS")!.EntryCommand.ClientOrderId;
        fixture.Broker.Orders[entryId] = Snapshot("broker-1", entryId, "partially_filled", 1m, 1.25m) with
        {
            Legs =
            [
                new BrokerOrderLegSnapshot("leg-1", "SPY260904C00650000", "filled", 1m, 1m)
            ]
        };

        MultiLegExecutionRecord record = await fixture.Lifecycle.AdvanceAsync(
            "OPTIONS-BROKEN-LEGS", CancellationToken.None);

        Assert.Equal(MultiLegExecutionState.ReconciliationFailed, record.State);
        Assert.Equal("ENTRY_BROKEN_LEG_FILL_RATIO", record.FailureReason);
    }

    [Fact]
    public async Task StaleUnfilledEntryIsCancelledWithoutAReplacementPost()
    {
        using var fixture = CreateFixture();
        Assert.True(fixture.Lifecycle.TryReserve(
            "OPTIONS-STALE", "spy-vertical", 1, 1.25m, 1.50m, 125m,
            TimeSpan.FromHours(1), EntryLegs()));
        await fixture.Lifecycle.AdvanceAsync("OPTIONS-STALE", CancellationToken.None);
        await fixture.Lifecycle.AdvanceAsync("OPTIONS-STALE", CancellationToken.None);
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));

        MultiLegExecutionRecord cancelled = await fixture.Lifecycle.AdvanceAsync(
            "OPTIONS-STALE", CancellationToken.None);

        Assert.Equal(MultiLegExecutionState.EntryRejected, cancelled.State);
        Assert.Equal("ENTRY_FILL_TIMEOUT_CANCELED", cancelled.FailureReason);
        Assert.Equal(1, fixture.Broker.SubmitCalls);
    }

    [Fact]
    public async Task ReconciliationFailsWhenBrokerRetainsOneSignedOptionLeg()
    {
        using var fixture = CreateFixture();
        Assert.True(fixture.Lifecycle.TryReserve(
            "OPTIONS-LEG-RECONCILE", "spy-vertical", 1, 1.25m, 1.50m, 125m,
            TimeSpan.FromHours(1), EntryLegs()));
        await fixture.Lifecycle.AdvanceAsync("OPTIONS-LEG-RECONCILE", CancellationToken.None);
        string entryId = fixture.Store.Find("OPTIONS-LEG-RECONCILE")!.EntryCommand.ClientOrderId;
        fixture.Broker.Orders[entryId] = Snapshot("broker-1", entryId, "filled", 1m, 1.25m);
        await fixture.Lifecycle.AdvanceAsync("OPTIONS-LEG-RECONCILE", CancellationToken.None);
        await fixture.Lifecycle.AdvanceAsync("OPTIONS-LEG-RECONCILE", CancellationToken.None);
        fixture.Clock.Advance(TimeSpan.FromHours(2));
        await fixture.Lifecycle.AdvanceAsync("OPTIONS-LEG-RECONCILE", CancellationToken.None);
        await fixture.Lifecycle.AdvanceAsync("OPTIONS-LEG-RECONCILE", CancellationToken.None);
        await fixture.Lifecycle.AdvanceAsync("OPTIONS-LEG-RECONCILE", CancellationToken.None);
        string exitId = fixture.Store.Find("OPTIONS-LEG-RECONCILE")!.ExitCommand.ClientOrderId;
        fixture.Broker.Orders[exitId] = Snapshot("broker-2", exitId, "filled", 1m, 1.50m);
        await fixture.Lifecycle.AdvanceAsync("OPTIONS-LEG-RECONCILE", CancellationToken.None);
        await fixture.Lifecycle.AdvanceAsync("OPTIONS-LEG-RECONCILE", CancellationToken.None);
        fixture.Broker.Positions = [new BrokerPositionSnapshot("SPY260904C00650000", 0, 1m, 1.25m)];

        MultiLegExecutionRecord failed = await fixture.Lifecycle.AdvanceAsync(
            "OPTIONS-LEG-RECONCILE", CancellationToken.None);

        Assert.Equal(MultiLegExecutionState.ReconciliationFailed, failed.State);
        Assert.Contains("legs=True", failed.FailureReason, StringComparison.Ordinal);
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

    [Fact]
    public async Task EmergencyFlattenUsesStableLegCloseIdentityAndVerifiesBrokerFlatness()
    {
        using var fixture = CreateFixture();
        Assert.True(fixture.Lifecycle.TryReserve(
            "OPTIONS-EMERGENCY", "spy-vertical", 1, 1.25m, 1.50m, 125m,
            TimeSpan.FromHours(1), EntryLegs()));
        fixture.Broker.Positions = [new BrokerPositionSnapshot("SPY260904C00650000", 7, 1m, 1.25m)];

        MultiLegExecutionLifecycle.EmergencyFlattenResult first = await fixture.Lifecycle.EmergencyFlattenAsync(
            "OPTIONS-EMERGENCY", CancellationToken.None);
        string expectedId = MultiLegExecutionLifecycle.DeterministicClientOrderId(
            "OPTIONS-EMERGENCY", "flatten:SPY260904C00650000");
        MultiLegExecutionLifecycle.EmergencyFlattenResult repeat = await fixture.Lifecycle.EmergencyFlattenAsync(
            "OPTIONS-EMERGENCY", CancellationToken.None);
        fixture.Broker.Positions = [];
        MultiLegExecutionLifecycle.EmergencyFlattenResult complete = await fixture.Lifecycle.EmergencyFlattenAsync(
            "OPTIONS-EMERGENCY", CancellationToken.None);

        Assert.True(first.Pending);
        Assert.True(repeat.Pending);
        Assert.Equal(1, fixture.Broker.DirectSubmitCalls);
        Assert.Equal(expectedId, fixture.Broker.DirectCommands.Single().ClientOrderId);
        Assert.True(complete.Complete);
        Assert.Equal(MultiLegExecutionState.EmergencyFlattened,
            fixture.Store.Find("OPTIONS-EMERGENCY")!.State);
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
        new(brokerId, clientId, status, quantity, price)
        {
            SubmittedAt = Start,
            Legs = string.Equals(status, "filled", StringComparison.OrdinalIgnoreCase)
                ? [
                    new BrokerOrderLegSnapshot($"{brokerId}-1", "SPY260904C00650000", "filled", quantity, price),
                    new BrokerOrderLegSnapshot($"{brokerId}-2", "SPY260904C00655000", "filled", quantity, price)
                ]
                : []
        };

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
        public int DirectSubmitCalls { get; private set; }
        public bool ThrowAfterAcceptance { get; set; }
        public bool ReturnUnknownAfterAcceptance { get; set; }
        public bool ReturnUnknownWithoutOrder { get; set; }
        public Action<MultiLegExecutionCommand>? BeforeSubmit { get; set; }
        public Dictionary<string, BrokerOrderSnapshot> Orders { get; } = [];
        public List<ExecutionCommand> DirectCommands { get; } = [];
        public IReadOnlyList<BrokerPositionSnapshot> Positions { get; set; } = [];

        public Task<BrokerSubmitResult> SubmitMultiLegAsync(
            MultiLegExecutionCommand command,
            CancellationToken cancellationToken)
        {
            SubmitCalls++;
            BeforeSubmit?.Invoke(command);
            string brokerId = $"broker-{SubmitCalls}";
            if (ReturnUnknownWithoutOrder)
            {
                ReturnUnknownWithoutOrder = false;
                return Task.FromResult(new BrokerSubmitResult(
                    BrokerSubmitState.Unknown, null, "BROKER_RESPONSE_LOST", $"request-{SubmitCalls}"));
            }
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
            CancellationToken cancellationToken) => Task.FromResult(Positions);

        public Task<BrokerSubmitResult> SubmitAsync(
            ExecutionCommand command,
            CancellationToken cancellationToken)
        {
            DirectSubmitCalls++;
            DirectCommands.Add(command);
            Orders[command.ClientOrderId] = new BrokerOrderSnapshot(
                $"close-{DirectSubmitCalls}", command.ClientOrderId, "accepted", 0, null);
            return Task.FromResult(new BrokerSubmitResult(BrokerSubmitState.Acknowledged,
                $"close-{DirectSubmitCalls}", null, null));
        }

        public Task<BrokerSubmitResult> CancelAsync(string brokerOrderId, CancellationToken cancellationToken) =>
            Task.FromResult(new BrokerSubmitResult(BrokerSubmitState.Acknowledged, brokerOrderId, null, null));
    }
}
