using QuantDesk.Domain.Execution;
using QuantDesk.Runtime.Execution;
using QuantDesk.Runtime.Persistence;
using QuantDesk.Runtime.Time;

namespace QuantDesk.Runtime.Tests.Execution;

/// <summary>
/// End-to-end coverage of the durable spot lifecycle: reservation before any POST, exactly-once
/// submission, recovery of an ambiguous submit by client order ID, a hold that survives restart,
/// and completion only on zero broker and internal exposure.
///
/// The spot lane was the only money path without these guarantees.
/// </summary>
public sealed class SpotExecutionLifecycleTests : IDisposable
{
    private const string ExecutionId = "SPOT-E2E-0001";
    private const string Symbol = "BTC/USD";
    private static readonly DateTimeOffset Start = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"qd-spot-{Guid.NewGuid():N}.json");
    private readonly MutableClock _clock = new(Start);

    [Fact]
    public void ReservationIsDurableBeforeAnyBrokerCall()
    {
        var broker = new FakeBroker();
        SpotExecutionLifecycle lifecycle = Build(broker);

        Assert.True(Reserve(lifecycle));

        Assert.True(File.Exists(_path));
        SpotExecutionRecord? record = Store().Find(ExecutionId);
        Assert.Equal(SpotExecutionState.EntryReserved, record!.State);
        Assert.Equal(0, broker.SubmitCount);
        Assert.StartsWith("qd-spot-", record.EntryClientOrderId, StringComparison.Ordinal);
        Assert.NotEqual(record.EntryClientOrderId, record.ExitClientOrderId);
    }

    [Fact]
    public async Task RepeatedAdvancementProducesAtMostOneEntrySubmission()
    {
        var broker = new FakeBroker();
        SpotExecutionLifecycle lifecycle = Build(broker);
        Reserve(lifecycle);

        for (int attempt = 0; attempt < 5; attempt++)
            await lifecycle.AdvanceAsync(ExecutionId, CancellationToken.None);

        Assert.Equal(1, broker.SubmitCount);
    }

    [Fact]
    public async Task ANonPaperBrokerCannotReserve()
    {
        var broker = new FakeBroker { IsPaper = false };
        Assert.False(Reserve(Build(broker)));
        Assert.Null(Store().Find(ExecutionId));
        await Task.CompletedTask;
    }

    [Fact]
    public void ASecondConcurrentExecutionOnTheSameSymbolIsRefused()
    {
        SpotExecutionLifecycle lifecycle = Build(new FakeBroker());
        Assert.True(Reserve(lifecycle));

        // A different execution id, same symbol, while the first is still open. The broker-position
        // check cannot see an order that has not yet become a position, so the store must refuse.
        bool second = lifecycle.TryReserve(
            "SPOT-E2E-0002", "crypto-long-momentum-v1", Symbol, 0, 1m, 20m, TimeSpan.FromMinutes(15));

        Assert.False(second);
        Assert.Single(Store().ListAll());
    }

    [Fact]
    public void ADifferentSymbolMayBeReservedConcurrently()
    {
        SpotExecutionLifecycle lifecycle = Build(new FakeBroker());
        Assert.True(Reserve(lifecycle));

        Assert.True(lifecycle.TryReserve(
            "SPOT-E2E-0003", "crypto-long-momentum-v1", "ETH/USD", 1, 1m, 20m, TimeSpan.FromMinutes(15)));
    }

    [Fact]
    public async Task ADuplicateExecutionIdIsRefused()
    {
        SpotExecutionLifecycle lifecycle = Build(new FakeBroker());
        Assert.True(Reserve(lifecycle));
        Assert.False(Reserve(lifecycle));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task AnAmbiguousSubmissionRecoversTheSameOrderByClientOrderId()
    {
        // The POST throws, so the lifecycle cannot know whether the order landed. It must ask the
        // broker for the exact ID rather than send a replacement.
        var broker = new FakeBroker { ThrowOnSubmit = true, ExistingOrderAfterThrow = true };
        SpotExecutionLifecycle lifecycle = Build(broker);
        Reserve(lifecycle);

        SpotExecutionRecord record = await lifecycle.AdvanceAsync(ExecutionId, CancellationToken.None);

        Assert.Equal(SpotExecutionState.EntryAccepted, record.State);
        Assert.Equal("broker-recovered", record.EntryBrokerOrderId);
        Assert.Equal(1, broker.SubmitCount);
    }

    [Fact]
    public async Task AnAmbiguousSubmissionThatNeverLandedReleasesTheClaimForRetry()
    {
        var broker = new FakeBroker { ThrowOnSubmit = true, ExistingOrderAfterThrow = false };
        SpotExecutionLifecycle lifecycle = Build(broker);
        Reserve(lifecycle);

        SpotExecutionRecord record = await lifecycle.AdvanceAsync(ExecutionId, CancellationToken.None);

        // Nothing at the broker means the order never existed, so the opportunity may be retried.
        Assert.Equal(SpotExecutionState.EntryReserved, record.State);
        Assert.Null(record.EntrySubmissionAttemptedAt);
    }

    [Fact]
    public async Task TheFullLifecycleReachesCompleteOnZeroExposure()
    {
        var broker = new FakeBroker();
        SpotExecutionLifecycle lifecycle = Build(broker);
        Reserve(lifecycle);

        await lifecycle.AdvanceAsync(ExecutionId, CancellationToken.None);      // submit entry
        broker.Order = Filled(broker.LastEntryClientOrderId!, 1m, 100m);
        await lifecycle.AdvanceAsync(ExecutionId, CancellationToken.None);      // entry filled
        SpotExecutionRecord holding = await lifecycle.AdvanceAsync(ExecutionId, CancellationToken.None);
        Assert.Equal(SpotExecutionState.Holding, holding.State);

        _clock.Advance(TimeSpan.FromMinutes(20));                               // hold elapses
        await lifecycle.AdvanceAsync(ExecutionId, CancellationToken.None);      // exit due
        broker.Order = null;
        await lifecycle.AdvanceAsync(ExecutionId, CancellationToken.None);      // submit exit
        broker.Order = Filled(Store().Find(ExecutionId)!.ExitClientOrderId, 1m, 101m);
        await lifecycle.AdvanceAsync(ExecutionId, CancellationToken.None);      // exit filled
        SpotExecutionRecord done = await lifecycle.AdvanceAsync(ExecutionId, CancellationToken.None);

        Assert.Equal(SpotExecutionState.Complete, done.State);
        Assert.Equal(0m, done.InternalOpenQuantity);
        Assert.NotNull(done.ReconciledAt);
        Assert.Equal(2, broker.SubmitCount);
    }

    [Fact]
    public async Task TheScheduledExitSurvivesARestart()
    {
        var broker = new FakeBroker();
        SpotExecutionLifecycle first = Build(broker);
        Reserve(first);
        await first.AdvanceAsync(ExecutionId, CancellationToken.None);
        broker.Order = Filled(broker.LastEntryClientOrderId!, 1m, 100m);
        await first.AdvanceAsync(ExecutionId, CancellationToken.None);
        await first.AdvanceAsync(ExecutionId, CancellationToken.None);
        DateTimeOffset scheduled = Store().Find(ExecutionId)!.ScheduledExitAt!.Value;

        // A brand-new lifecycle over the same store is what a restart looks like.
        SpotExecutionLifecycle restarted = Build(broker);
        await restarted.RecoverAllAsync(CancellationToken.None);

        // The exit time must not restart with the process.
        Assert.Equal(scheduled, Store().Find(ExecutionId)!.ScheduledExitAt);
    }

    [Fact]
    public async Task ACancelledEntryThatPartiallyFilledStillExitsRatherThanBeingAbandoned()
    {
        var broker = new FakeBroker();
        SpotExecutionLifecycle lifecycle = Build(broker);
        Reserve(lifecycle);
        await lifecycle.AdvanceAsync(ExecutionId, CancellationToken.None);

        broker.Order = new BrokerOrderSnapshot(
            "broker-1", broker.LastEntryClientOrderId!, "canceled", 0.4m, 100m);
        SpotExecutionRecord record = await lifecycle.AdvanceAsync(ExecutionId, CancellationToken.None);

        // Exposure exists, so the record must proceed to exit, not terminate as failed.
        Assert.Equal(SpotExecutionState.EntryFilled, record.State);
        Assert.Equal(0.4m, record.InternalOpenQuantity);
    }

    [Fact]
    public async Task ACancelledEntryWithNoFillFailsCleanly()
    {
        var broker = new FakeBroker();
        SpotExecutionLifecycle lifecycle = Build(broker);
        Reserve(lifecycle);
        await lifecycle.AdvanceAsync(ExecutionId, CancellationToken.None);

        broker.Order = new BrokerOrderSnapshot(
            "broker-1", broker.LastEntryClientOrderId!, "canceled", 0m, null);
        SpotExecutionRecord record = await lifecycle.AdvanceAsync(ExecutionId, CancellationToken.None);

        Assert.Equal(SpotExecutionState.Failed, record.State);
        Assert.True(record.IsTerminal);
    }

    [Fact]
    public async Task ABrokerPositionWithoutInternalExposureBlocksCompletion()
    {
        var broker = new FakeBroker { Positions = [new BrokerPositionSnapshot(Symbol, 0, 5m, 100m)] };
        SpotExecutionLifecycle lifecycle = Build(broker);
        Reserve(lifecycle);
        await lifecycle.AdvanceAsync(ExecutionId, CancellationToken.None);
        broker.Order = Filled(broker.LastEntryClientOrderId!, 1m, 100m);
        await lifecycle.AdvanceAsync(ExecutionId, CancellationToken.None);
        await lifecycle.AdvanceAsync(ExecutionId, CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(20));
        await lifecycle.AdvanceAsync(ExecutionId, CancellationToken.None);
        broker.Order = null;
        await lifecycle.AdvanceAsync(ExecutionId, CancellationToken.None);
        broker.Order = Filled(Store().Find(ExecutionId)!.ExitClientOrderId, 1m, 101m);
        await lifecycle.AdvanceAsync(ExecutionId, CancellationToken.None);

        SpotExecutionRecord record = await lifecycle.AdvanceAsync(ExecutionId, CancellationToken.None);

        // Unexplained broker exposure must never be reported as a clean completion.
        Assert.NotEqual(SpotExecutionState.Complete, record.State);
        Assert.Equal("BROKER_POSITION_WITHOUT_INTERNAL_EXPOSURE", record.FailureReason);
    }

    [Fact]
    public void ARecordClaimingAModeOtherThanPaperIsRefusedOnLoad()
    {
        // Write a genuine record, then tamper only with the mode, so the test exercises the guard
        // rather than the JSON parser.
        Reserve(Build(new FakeBroker()));
        string tampered = File.ReadAllText(_path)
            .Replace("\"PAPER\"", "\"LIVE\"", StringComparison.Ordinal);
        File.WriteAllText(_path, tampered);

        Assert.Throws<InvalidDataException>(() => Store().ListAll());
    }

    [Fact]
    public void ACorruptStoreFailsLoudlyRatherThanSilentlyLosingRecords()
    {
        File.WriteAllText(_path, "{ this is not json");

        // Returning an empty set here would look like "no executions to recover", which is the
        // most dangerous possible misreading of a corrupt store.
        Assert.ThrowsAny<Exception>(() => Store().ListAll());
    }

    private bool Reserve(SpotExecutionLifecycle lifecycle) => lifecycle.TryReserve(
        ExecutionId, "crypto-long-momentum-v1", Symbol, 0, 1m, 20m, TimeSpan.FromMinutes(15));

    private SpotExecutionStore Store() => new(_path);

    private SpotExecutionLifecycle Build(FakeBroker broker) =>
        new(broker, Store(), _clock, TimeSpan.FromSeconds(30));

    private static BrokerOrderSnapshot Filled(string clientOrderId, decimal quantity, decimal price) =>
        new("broker-1", clientOrderId, "filled", quantity, price);

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private sealed class MutableClock(DateTimeOffset start) : IRuntimeClock
    {
        private DateTimeOffset _now = start;
        public DateTimeOffset UtcNow => _now;
        public long MonotonicTimestamp => _now.ToUnixTimeMilliseconds() * 1_000;

        public double ElapsedMilliseconds(long fromTimestamp, long toTimestamp) =>
            (toTimestamp - fromTimestamp) / 1_000d;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    private sealed class FakeBroker : IBrokerExecutionGateway
    {
        public int SubmitCount { get; private set; }
        public string? LastEntryClientOrderId { get; private set; }
        public BrokerOrderSnapshot? Order { get; set; }
        public IReadOnlyList<BrokerPositionSnapshot> Positions { get; init; } = [];
        public bool IsPaper { get; init; } = true;
        public bool ThrowOnSubmit { get; init; }
        public bool ExistingOrderAfterThrow { get; init; }

        public bool IsPaperEnvironment => IsPaper;

        public Task<BrokerSubmitResult> SubmitAsync(
            ExecutionCommand command, CancellationToken cancellationToken)
        {
            SubmitCount++;
            LastEntryClientOrderId ??= command.ClientOrderId;
            if (ThrowOnSubmit) throw new HttpRequestException("connection reset");
            return Task.FromResult(new BrokerSubmitResult(
                BrokerSubmitState.Acknowledged, "broker-1", null, null));
        }

        public Task<BrokerOrderSnapshot?> FindByClientOrderIdAsync(
            string clientOrderId, CancellationToken cancellationToken)
        {
            if (ThrowOnSubmit)
            {
                return Task.FromResult(ExistingOrderAfterThrow
                    ? new BrokerOrderSnapshot("broker-recovered", clientOrderId, "new", 0m, null)
                    : null);
            }

            return Task.FromResult(
                Order is not null && Order.ClientOrderId == clientOrderId ? Order : null);
        }

        public Task<IReadOnlyList<BrokerPositionSnapshot>> ListPositionsAsync(
            CancellationToken cancellationToken) => Task.FromResult(Positions);
    }
}
