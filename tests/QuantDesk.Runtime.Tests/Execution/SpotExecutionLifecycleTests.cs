using QuantDesk.Domain.Execution;
using QuantDesk.Domain.Trading;
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
        broker.HoldAfterFill(Symbol, 1m, 100m);                                // fee taken in kind
        await lifecycle.AdvanceAsync(ExecutionId, CancellationToken.None);      // entry filled
        SpotExecutionRecord holding = await lifecycle.AdvanceAsync(ExecutionId, CancellationToken.None);
        Assert.Equal(SpotExecutionState.Holding, holding.State);

        _clock.Advance(TimeSpan.FromMinutes(20));                               // hold elapses
        await lifecycle.AdvanceAsync(ExecutionId, CancellationToken.None);      // exit due
        broker.Order = null;
        await lifecycle.AdvanceAsync(ExecutionId, CancellationToken.None);      // submit exit
        broker.Order = Filled(Store().Find(ExecutionId)!.ExitClientOrderId, 1m, 101m);
        broker.Positions = [];                                                 // flat after the exit
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

    [Fact]
    public async Task AnInterruptEndsTheHoldBeforeItsTimer()
    {
        // Before this, the clock was the only thing that ended a hold. A position whose research had
        // been retracted, or one already past its defined maximum loss, ran to its timer regardless.
        var broker = new FakeBroker();
        SpotExecutionLifecycle lifecycle = Build(broker, new AlwaysExit("ArtifactRetracted:test"));
        await ReachHoldingAsync(lifecycle, broker);

        // No time passes at all -- the timer has 15 minutes left.
        SpotExecutionRecord interrupted = await lifecycle.AdvanceAsync(ExecutionId, CancellationToken.None);

        Assert.Equal(SpotExecutionState.ExitDue, interrupted.State);
        Assert.Equal("ArtifactRetracted:test", interrupted.EarlyExitReason);
    }

    [Fact]
    public async Task AnInterruptThatNeverFiresLeavesTheTimerInCharge()
    {
        var broker = new FakeBroker();
        SpotExecutionLifecycle lifecycle = Build(broker, new NeverExit());
        await ReachHoldingAsync(lifecycle, broker);

        Assert.Equal(SpotExecutionState.Holding,
            (await lifecycle.AdvanceAsync(ExecutionId, CancellationToken.None)).State);

        _clock.Advance(TimeSpan.FromMinutes(20));

        Assert.Equal(SpotExecutionState.ExitDue,
            (await lifecycle.AdvanceAsync(ExecutionId, CancellationToken.None)).State);
    }

    [Fact]
    public async Task AnInterruptCannotExtendAHoldPastItsDeadline()
    {
        // The safety contract. An interrupt may only pull the exit forward, so an implementation
        // that is broken, wrong, or throwing cannot pin a position open past the deadline its
        // reservation was taken against. Here the interrupt actively says "keep holding" and the
        // timer overrules it.
        var broker = new FakeBroker();
        SpotExecutionLifecycle lifecycle = Build(broker, new NeverExit());
        await ReachHoldingAsync(lifecycle, broker);

        _clock.Advance(TimeSpan.FromMinutes(20));
        SpotExecutionRecord due = await lifecycle.AdvanceAsync(ExecutionId, CancellationToken.None);

        Assert.Equal(SpotExecutionState.ExitDue, due.State);
        Assert.Null(due.EarlyExitReason);
    }

    [Fact]
    public async Task AThrowingInterruptCannotStrandAPositionPastItsDeadline()
    {
        var broker = new FakeBroker();
        SpotExecutionLifecycle lifecycle = Build(broker, new Throwing());
        await ReachHoldingAsync(lifecycle, broker);

        _clock.Advance(TimeSpan.FromMinutes(20));

        // The timer is checked first, so the deadline is honoured without the interrupt running.
        Assert.Equal(SpotExecutionState.ExitDue,
            (await lifecycle.AdvanceAsync(ExecutionId, CancellationToken.None)).State);
    }

    [Fact]
    public void TheAuthorisingArtifactIsPersistedWithTheReservation()
    {
        // A position that cannot name what licensed it is the state binding exists to prevent, and
        // the binding has to survive a restart to be worth anything.
        SpotExecutionLifecycle lifecycle = Build(new FakeBroker());
        var ownership = new PositionOwnership(
            "artifact-1", "v3", "hash-abc", "momentum", Start);

        Assert.True(lifecycle.TryReserve(
            ExecutionId, "crypto-long-momentum-v1", Symbol, 0, 1m, 20m,
            TimeSpan.FromMinutes(15), ownership: ownership));

        // Re-read from disk, not from memory.
        PositionOwnership? persisted = new SpotExecutionStore(_path).Find(ExecutionId)!.Ownership;
        Assert.Equal(ownership, persisted);
    }

    private async Task ReachHoldingAsync(SpotExecutionLifecycle lifecycle, FakeBroker broker)
    {
        Reserve(lifecycle);
        await lifecycle.AdvanceAsync(ExecutionId, CancellationToken.None);
        broker.Order = Filled(broker.LastEntryClientOrderId!, 1m, 100m);
        broker.HoldAfterFill(Symbol, 1m, 100m);
        await lifecycle.AdvanceAsync(ExecutionId, CancellationToken.None);
        SpotExecutionRecord holding = await lifecycle.AdvanceAsync(ExecutionId, CancellationToken.None);
        Assert.Equal(SpotExecutionState.Holding, holding.State);
    }

    private sealed class AlwaysExit(string reason) : IHoldInterrupt
    {
        public HoldInterrupt Evaluate(in HeldPosition position) => HoldInterrupt.Now(reason);
    }

    private sealed class NeverExit : IHoldInterrupt
    {
        public HoldInterrupt Evaluate(in HeldPosition position) => HoldInterrupt.None;
    }

    private sealed class Throwing : IHoldInterrupt
    {
        public HoldInterrupt Evaluate(in HeldPosition position) =>
            throw new InvalidOperationException("a broken interrupt must not strand a position");
    }

    [Fact]
    public async Task ACompletedRoundTripCanSayWhatItCost()
    {
        // The chain this closes. The cost dataset that gates every decision was derived only from
        // the diagnostic lane, and its records never carried a decision price -- 0 of 68 -- so the
        // estimator could never produce a dataset at all. If the autonomous lane is the one
        // trading, it has to measure its own round trips or the figure gating them goes stale while
        // still looking measured.
        var broker = new FakeBroker { AccountEquity = 100_000m };
        SpotExecutionLifecycle lifecycle = Build(broker, referencePrices: new StubMarker(101m));

        Assert.True(lifecycle.TryReserve(
            ExecutionId, "crypto-long-momentum-v1", Symbol, 0, 1m, 20m, TimeSpan.FromMinutes(15),
            entryReferencePrice: 100m, accountEquityBefore: 100_000m));

        await ReachHoldingAsync(lifecycle, broker);
        _clock.Advance(TimeSpan.FromMinutes(20));
        await lifecycle.AdvanceAsync(ExecutionId, CancellationToken.None);   // exit due
        broker.Order = null;
        await lifecycle.AdvanceAsync(ExecutionId, CancellationToken.None);   // submit exit
        broker.Order = Filled(Store().Find(ExecutionId)!.ExitClientOrderId, 1m, 100.9m);
        broker.Positions = [];                                               // flat after the exit
        broker.AccountEquity = 100_000.60m;                                  // 40 cents of cost
        await lifecycle.AdvanceAsync(ExecutionId, CancellationToken.None);   // exit filled
        SpotExecutionRecord done = await lifecycle.AdvanceAsync(ExecutionId, CancellationToken.None);

        Assert.Equal(SpotExecutionState.Complete, done.State);
        Assert.Equal(100m, done.EntryReferencePrice);
        Assert.Equal(101m, done.ExitReferencePrice);
        Assert.Equal(100_000m, done.AccountEquityBefore);
        Assert.Equal(100_000.60m, done.AccountEquityAfter);

        // A frictionless round trip at the decision prices earns 1.00; the account gained 0.60, so
        // the trip cost 0.40 on 100 of notional -- 40 bps. Fills alone would never show that.
        Assert.Equal(0.60m, done.RealisedAccountPnl);
    }

    [Fact]
    public async Task ARoundTripWithNoDecisionPriceStillCompletesAndSimplyCannotTestify()
    {
        // Backward compatible. Records written before this capture existed have no reference price
        // and are skipped by the estimator rather than approximated from their fills, which would
        // see only the fee and report roughly half the true cost.
        var broker = new FakeBroker();
        SpotExecutionLifecycle lifecycle = Build(broker);
        Reserve(lifecycle);
        await ReachHoldingAsync(lifecycle, broker);
        _clock.Advance(TimeSpan.FromMinutes(20));
        await lifecycle.AdvanceAsync(ExecutionId, CancellationToken.None);
        broker.Order = null;
        await lifecycle.AdvanceAsync(ExecutionId, CancellationToken.None);
        broker.Order = Filled(Store().Find(ExecutionId)!.ExitClientOrderId, 1m, 101m);
        broker.Positions = [];                                               // flat after the exit
        await lifecycle.AdvanceAsync(ExecutionId, CancellationToken.None);
        SpotExecutionRecord done = await lifecycle.AdvanceAsync(ExecutionId, CancellationToken.None);

        Assert.Equal(SpotExecutionState.Complete, done.State);
        Assert.Null(done.EntryReferencePrice);
    }

    private sealed class StubMarker(decimal? mid) : IHeldPositionMarker
    {
        public decimal? CurrentMid(string symbol) => mid;
    }

    [Fact]
    public async Task TheExitSellsWhatIsHeldRatherThanWhatWasBought()
    {
        // The venue charges its spot crypto fee in kind, taking it out of the delivered quantity.
        // An entry that filled 1.0 leaves slightly less than 1.0 in the account, so asking to sell
        // the filled quantity asks for more than exists and is refused:
        //   "insufficient balance for AAVE (requested: 1.54344806, available: 1.539589439)".
        //
        // A rejected exit correctly returns to ExitDue and retries, which turned that into a
        // permanent failure -- every retry asked for the same impossible quantity, so the position
        // could not be closed by the managed path and its holding period was not actually bounded.
        var broker = new FakeBroker();
        SpotExecutionLifecycle lifecycle = Build(broker);
        await ReachHoldingAsync(lifecycle, broker);

        _clock.Advance(TimeSpan.FromMinutes(20));
        await lifecycle.AdvanceAsync(ExecutionId, CancellationToken.None);   // exit due
        broker.Order = null;
        await lifecycle.AdvanceAsync(ExecutionId, CancellationToken.None);   // submit exit

        // ReachHoldingAsync fills 1.0 and the fake holds 0.9975 of it, as the venue would.
        Assert.Equal(0.9975m, broker.LastExitQuantity);
        Assert.True(broker.LastExitQuantity < 1m,
            "selling the filled quantity would be refused for insufficient balance");
    }

    [Fact]
    public async Task AnExitFindsNothingToSellWhenTheBrokerHoldsNothing()
    {
        // Reconciliation decides whether being flat is correct; the exit simply has no work.
        var broker = new FakeBroker();
        SpotExecutionLifecycle lifecycle = Build(broker);
        await ReachHoldingAsync(lifecycle, broker);
        broker.Positions = [];

        _clock.Advance(TimeSpan.FromMinutes(20));
        await lifecycle.AdvanceAsync(ExecutionId, CancellationToken.None);
        SpotExecutionRecord record = await lifecycle.AdvanceAsync(ExecutionId, CancellationToken.None);

        Assert.Equal(SpotExecutionState.Reconciling, record.State);
    }

    [Fact]
    public async Task ACryptoPositionIsFoundDespiteTheVenueSpellingItDifferently()
    {
        // Alpaca returns spot crypto positions without the separator -- "BTCUSD" where the system
        // says "BTC/USD". An ordinal comparison never matches, so every crypto position looks like
        // it does not exist: the exit concludes there is nothing to sell and abandons a live
        // position to reconciliation, which then cannot see it either.
        var broker = new FakeBroker();
        SpotExecutionLifecycle lifecycle = Build(broker);
        await ReachHoldingAsync(lifecycle, broker);
        broker.Positions = [new BrokerPositionSnapshot("BTCUSD", 0, 0.9975m, 100m)];

        _clock.Advance(TimeSpan.FromMinutes(20));
        await lifecycle.AdvanceAsync(ExecutionId, CancellationToken.None);
        SpotExecutionRecord record = await lifecycle.AdvanceAsync(ExecutionId, CancellationToken.None);

        Assert.NotEqual(SpotExecutionState.Reconciling, record.State);
        Assert.Equal(0.9975m, broker.LastExitQuantity);
    }

    [Fact]
    public async Task AnExitThatNeverReachedTheVenueIsRetriedRatherThanLeftInFlight()
    {
        // The state AAVE sat in for over an hour: its exit was refused, no order existed under the
        // deterministic ID, and tracking returned the record untouched -- so nothing retried and
        // nothing complained while the position stayed open past its deadline.
        var broker = new FakeBroker();
        SpotExecutionLifecycle lifecycle = Build(broker);
        await ReachHoldingAsync(lifecycle, broker);
        _clock.Advance(TimeSpan.FromMinutes(20));
        await lifecycle.AdvanceAsync(ExecutionId, CancellationToken.None);   // exit due
        await lifecycle.AdvanceAsync(ExecutionId, CancellationToken.None);   // submitted
        broker.Order = null;                                                 // venue has no such order

        SpotExecutionRecord record = await lifecycle.AdvanceAsync(ExecutionId, CancellationToken.None);

        Assert.Equal(SpotExecutionState.ExitDue, record.State);
    }

    private bool Reserve(SpotExecutionLifecycle lifecycle) => lifecycle.TryReserve(
        ExecutionId, "crypto-long-momentum-v1", Symbol, 0, 1m, 20m, TimeSpan.FromMinutes(15));

    private SpotExecutionStore Store() => new(_path);

    private SpotExecutionLifecycle Build(
        FakeBroker broker,
        IHoldInterrupt? interrupt = null,
        IHeldPositionMarker? referencePrices = null) =>
        new(broker, Store(), _clock, TimeSpan.FromSeconds(30), interrupt, referencePrices);

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

        /// <summary>Quantity the most recent sell asked for, which is what the venue enforces.</summary>
        public decimal LastExitQuantity { get; private set; }
        public BrokerOrderSnapshot? Order { get; set; }
        public IReadOnlyList<BrokerPositionSnapshot> Positions { get; set; } = [];

        /// <summary>
        /// Records a filled entry as a held position, less the fee the venue takes in kind.
        ///
        /// Alpaca deducts its spot crypto fee from the delivered quantity, so the account holds
        /// slightly less than the entry filled. Modelling that here is what makes these tests
        /// exercise the exit sizing at all: with a fake that reports the full filled quantity, an
        /// exit asking to sell it would succeed in the test and be refused by the venue.
        /// </summary>
        public void HoldAfterFill(string symbol, decimal filledQuantity, decimal price) =>
            Positions = [new BrokerPositionSnapshot(symbol, 0, filledQuantity * 0.9975m, price)];
        public bool IsPaper { get; init; } = true;
        public bool ThrowOnSubmit { get; init; }

        /// <summary>Account equity the lifecycle reads when a round trip reconciles flat.</summary>
        public decimal? AccountEquity { get; set; }

        public Task<BrokerAccountSnapshot?> GetAccountAsync(CancellationToken cancellationToken) =>
            Task.FromResult<BrokerAccountSnapshot?>(AccountEquity is { } equity
                ? new BrokerAccountSnapshot("acct", "ACTIVE", equity, equity, false, false)
                : null);
        public bool ExistingOrderAfterThrow { get; init; }

        public bool IsPaperEnvironment => IsPaper;

        public Task<BrokerSubmitResult> SubmitAsync(
            ExecutionCommand command, CancellationToken cancellationToken)
        {
            SubmitCount++;
            LastEntryClientOrderId ??= command.ClientOrderId;
            if (command.Side == OrderSide.Sell) LastExitQuantity = command.Quantity;
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
