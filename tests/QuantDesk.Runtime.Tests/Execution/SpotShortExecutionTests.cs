using QuantDesk.Domain.Execution;
using QuantDesk.Domain.Trading;
using QuantDesk.Runtime.Execution;
using QuantDesk.Runtime.Persistence;
using QuantDesk.Runtime.Time;

namespace QuantDesk.Runtime.Tests.Execution;

/// <summary>
/// The execution half of direction: a short that is actually held short.
///
/// Rules learned to say Short before anything could act on one, and the pipeline refused every
/// bearish signal rather than send it to a lane that would have opened a long on it. These are the
/// four places that assumed one direction and would each have failed silently in the other:
/// the side of the opening order, the side of the closing order, which way the entry fence reads a
/// price move, and the sign of realisable profit.
///
/// None of them throws when it is wrong. A short opened as a buy is a live position pointing the
/// wrong way; a short marked with a long's arithmetic reports a loss as a gain and holds it.
/// </summary>
public sealed class SpotShortExecutionTests : IDisposable
{
    private const string ExecutionId = "SPOT-SHORT-0001";
    private const string Symbol = "SPY";
    private static readonly DateTimeOffset Start = new(2026, 9, 3, 14, 0, 0, TimeSpan.Zero);

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"qd-short-{Guid.NewGuid():N}.json");
    private readonly MutableClock _clock = new(Start);

    [Fact]
    public async Task AShortOpensWithASell()
    {
        var broker = new RecordingBroker();
        SpotExecutionLifecycle lifecycle = Build(broker);
        Assert.True(Reserve(lifecycle, SignalDirection.Short));

        await lifecycle.AdvanceAsync(ExecutionId, CancellationToken.None);

        ExecutionCommand entry = Assert.Single(broker.Submitted);
        Assert.Equal(OrderSide.Sell, entry.Side);
        Assert.Equal(PositionIntent.Open, entry.PositionIntent);
    }

    [Fact]
    public async Task ALongStillOpensWithABuy()
    {
        // The regression guard. Every record written before direction existed is a long, and a
        // record loaded without the field must read as one rather than as the enum's own default.
        var broker = new RecordingBroker();
        SpotExecutionLifecycle lifecycle = Build(broker);
        Assert.True(Reserve(lifecycle, SignalDirection.Long));

        await lifecycle.AdvanceAsync(ExecutionId, CancellationToken.None);

        Assert.Equal(OrderSide.Buy, Assert.Single(broker.Submitted).Side);
    }

    [Fact]
    public void ARecordWrittenBeforeDirectionExistedLoadsAsALong()
    {
        var store = new SpotExecutionStore(_path);
        Assert.True(store.TryCreate(new SpotExecutionRecord(
            ExecutionId, "s", Symbol, 0, SpotExecutionState.EntryReserved,
            "qd-spot-entry", "qd-spot-exit", 1m, Start, Start)));

        Assert.Equal(SignalDirection.Long, new SpotExecutionStore(_path).Find(ExecutionId)!.Direction);
    }

    [Fact]
    public void NoneIsNeverReserved()
    {
        // None means a rule looked and declined to commit. Reserving on it would open exposure
        // nothing asked for, and it is exactly what a record deserialised without a direction would
        // carry if the default were the enum's own.
        SpotExecutionLifecycle lifecycle = Build(new RecordingBroker());

        Assert.False(Reserve(lifecycle, SignalDirection.None));
        Assert.Null(new SpotExecutionStore(_path).Find(ExecutionId));
    }

    [Fact]
    public async Task AShortClosesWithABuyForWhatTheAccountIsActuallyShort()
    {
        var broker = new RecordingBroker();
        SpotExecutionLifecycle lifecycle = Build(broker);
        Assert.True(Reserve(lifecycle, SignalDirection.Short));
        SpotExecutionRecord opened =
            await lifecycle.AdvanceAsync(ExecutionId, CancellationToken.None);

        // The entry fills, and the venue reports a short as a negative quantity.
        broker.Order = new BrokerOrderSnapshot(
            "broker-1", opened.EntryClientOrderId, "filled", 4m, 100m);
        broker.Positions = [new BrokerPositionSnapshot(Symbol, 0, -4m, 100m)];
        await lifecycle.AdvanceAsync(ExecutionId, CancellationToken.None);   // fills
        await lifecycle.AdvanceAsync(ExecutionId, CancellationToken.None);   // starts the hold

        _clock.Advance(TimeSpan.FromMinutes(30));
        await lifecycle.AdvanceAsync(ExecutionId, CancellationToken.None);   // hold expires
        broker.Order = null;
        await lifecycle.AdvanceAsync(ExecutionId, CancellationToken.None);   // submits the exit

        ExecutionCommand exit = Assert.Single(
            broker.Submitted, command => command.PositionIntent == PositionIntent.Close);
        Assert.Equal(OrderSide.Buy, exit.Side);

        // Positive four, not negative four. The venue's sign lives in the position report; the
        // order asks for a magnitude and a side.
        Assert.Equal(4m, exit.Quantity);
    }

    [Fact]
    public async Task TheEntryFenceRefusesAShortOnlyWhenPriceHasFallenAwayFromIt()
    {
        // A rise is adverse to a long and favourable to a short. Reading the long's sign for both
        // would have refused every short into a falling market -- the entries a short exists to
        // take -- and admitted the ones whose move had already happened.
        var favourable = new RecordingBroker();
        SpotExecutionLifecycle rising = Build(
            favourable, referencePrices: new FixedMid(Symbol, 101m));
        Assert.True(rising.TryReserve(
            ExecutionId, "s", Symbol, 0, 1m, 20m, TimeSpan.FromMinutes(15),
            entryReferencePrice: 100m, direction: SignalDirection.Short));

        // +100 bps: the short is already in profit before it opens, which is not a refusal.
        SpotExecutionRecord admitted =
            await rising.AdvanceAsync(ExecutionId, CancellationToken.None);
        Assert.NotEqual(SpotExecutionState.Failed, admitted.State);

        SpotExecutionLifecycle falling = Build(
            new RecordingBroker(), referencePrices: new FixedMid("QQQ", 99m));
        Assert.True(falling.TryReserve(
            "SPOT-SHORT-0002", "s", "QQQ", 0, 1m, 20m, TimeSpan.FromMinutes(15),
            entryReferencePrice: 100m, direction: SignalDirection.Short));

        SpotExecutionRecord fenced =
            await falling.AdvanceAsync("SPOT-SHORT-0002", CancellationToken.None);
        Assert.Equal(SpotExecutionState.Failed, fenced.State);
        Assert.StartsWith("ENTRY_FENCE_ADVERSE_MOVE", fenced.FailureReason!, StringComparison.Ordinal);
    }

    [Fact]
    public void AShortProfitsWhenPriceFalls()
    {
        HeldPosition position = Short(entry: 100m, quantity: 10m);

        // Sold ten at 100, buying back at 95: fifty of profit, less the exit cost.
        Assert.Equal(50m, position.RealisableProfit(95m)!.Value, precision: 6);
        Assert.Equal(-50m, position.RealisableProfit(105m)!.Value, precision: 6);
    }

    [Fact]
    public void TheExitCostFallsOnTheBuybackNotTheSale()
    {
        // A short is not a long with the sign flipped at the end: the closing leg is a purchase, so
        // the cost rate makes it dearer rather than making a sale worth less. Negating a long's
        // answer would credit the short two exit fees it never earned.
        HeldPosition position = Short(entry: 100m, quantity: 10m) with { ExitCostRate = 0.0025m };

        // Buyback of 950 costs 950 * 1.0025 = 952.375, against 1000 received.
        Assert.Equal(47.625m, position.RealisableProfit(95m)!.Value, precision: 6);
    }

    [Fact]
    public void ALongsArithmeticIsUnchanged()
    {
        HeldPosition position = Short(entry: 100m, quantity: 10m) with
        {
            Direction = SignalDirection.Long,
            ExitCostRate = 0.0025m,
        };

        // Bought ten at 100, selling at 105: proceeds 1050 * 0.9975 = 1047.375.
        Assert.Equal(47.375m, position.RealisableProfit(105m)!.Value, precision: 6);
    }

    // ------------------------------------------------------------------------------- fixtures

    private static HeldPosition Short(decimal entry, decimal quantity) => new(
        ExecutionId, Symbol, quantity, entry, DefinedMaximumLoss: 100m,
        Ownership: null, EarliestLegExpiry: null, Direction: SignalDirection.Short);

    private bool Reserve(SpotExecutionLifecycle lifecycle, SignalDirection direction) =>
        lifecycle.TryReserve(
            ExecutionId, "s", Symbol, 0, 4m, 20m, TimeSpan.FromMinutes(15), direction: direction);

    private SpotExecutionLifecycle Build(
        RecordingBroker broker, IHeldPositionMarker? referencePrices = null) =>
        new(broker, new SpotExecutionStore(_path), _clock, TimeSpan.FromSeconds(30),
            holdInterrupt: null, referencePrices);

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private sealed class FixedMid(string symbol, decimal mid) : IHeldPositionMarker
    {
        public decimal? CurrentMid(string forSymbol) =>
            string.Equals(forSymbol, symbol, StringComparison.OrdinalIgnoreCase) ? mid : null;

        public double? CurrentRelativeSpread(string forSymbol) => 0d;
    }

    private sealed class RecordingBroker : IBrokerExecutionGateway
    {
        public List<ExecutionCommand> Submitted { get; } = [];
        public BrokerOrderSnapshot? Order { get; set; }
        public IReadOnlyList<BrokerPositionSnapshot> Positions { get; set; } = [];

        public bool IsPaperEnvironment => true;

        public Task<BrokerSubmitResult> SubmitAsync(
            ExecutionCommand command, CancellationToken cancellationToken)
        {
            Submitted.Add(command);
            return Task.FromResult(new BrokerSubmitResult(
                BrokerSubmitState.Acknowledged, "broker-1", null, null));
        }

        public Task<BrokerOrderSnapshot?> FindByClientOrderIdAsync(
            string clientOrderId, CancellationToken cancellationToken) =>
            Task.FromResult(Order is not null && Order.ClientOrderId == clientOrderId ? Order : null);

        public Task<IReadOnlyList<BrokerPositionSnapshot>> ListPositionsAsync(
            CancellationToken cancellationToken) => Task.FromResult(Positions);

        public Task<BrokerAccountSnapshot?> GetAccountAsync(CancellationToken cancellationToken) =>
            Task.FromResult<BrokerAccountSnapshot?>(null);
    }

    private sealed class MutableClock(DateTimeOffset start) : IRuntimeClock
    {
        private DateTimeOffset _now = start;
        public DateTimeOffset UtcNow => _now;
        public long MonotonicTimestamp => _now.ToUnixTimeMilliseconds() * 1_000;

        public double ElapsedMilliseconds(long fromTimestamp, long toTimestamp) =>
            (toTimestamp - fromTimestamp) / 1_000d;

        public long MonotonicTicksFor(TimeSpan duration) =>
            duration <= TimeSpan.Zero ? 0L : (long)(duration.TotalMilliseconds * 1_000d);

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}
