using QuantDesk.Domain.Portfolio;
using QuantDesk.Domain.Trading;

namespace QuantDesk.Runtime.Portfolio;

public sealed record OrderAttribution(
    string StrategyId,
    string ExitPolicyVersion,
    long EpisodeId,
    int PolicyVersion,
    int[] ForecastIds);

public sealed class PortfolioLedger
{
    private readonly Lock _gate = new();
    private readonly Dictionary<int, MutablePosition> _positions = [];
    private readonly Dictionary<string, OrderAttribution> _orderAttributions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _processedFillIds = new(StringComparer.Ordinal);
    private readonly List<VirtualStrategyLot> _lots = [];
    private PortfolioSnapshot _snapshot;
    private long _nextLotId;

    public PortfolioLedger(PortfolioSnapshot initialSnapshot)
    {
        _snapshot = initialSnapshot;
    }

    public PortfolioSnapshot Snapshot()
    {
        lock (_gate) return _snapshot;
    }

    public IReadOnlyList<VirtualStrategyLot> Lots()
    {
        lock (_gate) return _lots.ToArray();
    }

    public void MarkToMarket(IReadOnlyDictionary<int, decimal> markPrices)
    {
        ArgumentNullException.ThrowIfNull(markPrices);
        lock (_gate)
        {
            decimal marketValue = 0;
            IReadOnlyList<PositionSnapshot> positions = _positions.Values
                .Where(position => position.Quantity != 0)
                .Select(position =>
                {
                    if (!markPrices.TryGetValue(position.InstrumentSlot, out decimal mark) || mark <= 0)
                        return position.Snapshot();
                    PositionSnapshot snapshot = position.Snapshot(mark);
                    marketValue += snapshot.Quantity * mark;
                    return snapshot;
                }).ToArray();
            _snapshot = _snapshot with
            {
                Version = _snapshot.Version + 1,
                Equity = _snapshot.Cash + new Domain.Numerics.Usd(marketValue),
                Positions = positions
            };
        }
    }

    public void RegisterOrderAttribution(string clientOrderId, OrderAttribution attribution)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientOrderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(attribution.StrategyId);

        lock (_gate)
        {
            if (!_orderAttributions.TryAdd(clientOrderId.Trim(), attribution))
            {
                throw new InvalidOperationException($"Order attribution already exists for '{clientOrderId}'.");
            }
        }
    }

    public bool ApplyFill(in NormalizedFill fill)
    {
        ValidateFill(fill);

        lock (_gate)
        {
            if (!_processedFillIds.Add(fill.FillId)) return false;

            OrderAttribution attribution = _orderAttributions.TryGetValue(
                fill.ClientOrderId,
                out OrderAttribution? registered)
                ? registered
                : new OrderAttribution("external", "unknown", 0, 0, []);

            MutablePosition position = GetOrCreatePosition(fill.InstrumentSlot, attribution);
            decimal signedQuantity = fill.Side == OrderSide.Buy ? fill.Quantity : -fill.Quantity;
            decimal cashDelta = signedQuantity * fill.Price;
            Domain.Numerics.Usd realizedDelta = position.Apply(signedQuantity, fill.Price, fill.EventUnixNanoseconds);
            _lots.Add(new VirtualStrategyLot(
                ++_nextLotId,
                fill.InstrumentSlot,
                attribution.StrategyId,
                signedQuantity,
                fill.Price,
                attribution.EpisodeId,
                attribution.PolicyVersion,
                attribution.ForecastIds.ToArray()));

            _snapshot = _snapshot with
            {
                Version = _snapshot.Version + 1,
                Cash = _snapshot.Cash - new Domain.Numerics.Usd(cashDelta),
                DailyPnl = _snapshot.DailyPnl + realizedDelta,
                CampaignPnl = _snapshot.CampaignPnl + realizedDelta,
                Positions = BuildSnapshots()
            };
            return true;
        }
    }

    private MutablePosition GetOrCreatePosition(int instrumentSlot, OrderAttribution attribution)
    {
        if (_positions.TryGetValue(instrumentSlot, out MutablePosition? existing)) return existing;

        var created = new MutablePosition(
            instrumentSlot,
            attribution.StrategyId,
            attribution.ExitPolicyVersion);
        _positions.Add(instrumentSlot, created);
        return created;
    }

    private IReadOnlyList<PositionSnapshot> BuildSnapshots() =>
        _positions.Values
            .Where(position => position.Quantity != 0)
            .Select(position => position.Snapshot())
            .ToArray();

    private static void ValidateFill(in NormalizedFill fill)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fill.ClientOrderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fill.BrokerOrderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fill.FillId);
        if (fill.InstrumentSlot < 0) throw new ArgumentOutOfRangeException(nameof(fill.InstrumentSlot));
        if (fill.Quantity <= 0) throw new ArgumentOutOfRangeException(nameof(fill.Quantity));
        if (fill.Price <= 0) throw new ArgumentOutOfRangeException(nameof(fill.Price));
        if (fill.EventUnixNanoseconds <= 0) throw new ArgumentOutOfRangeException(nameof(fill.EventUnixNanoseconds));
    }

    private sealed class MutablePosition(
        int instrumentSlot,
        string strategyId,
        string exitPolicyVersion)
    {
        public int InstrumentSlot { get; } = instrumentSlot;
        public string StrategyId { get; } = strategyId;
        public string ExitPolicyVersion { get; } = exitPolicyVersion;
        public decimal Quantity { get; private set; }
        public decimal AveragePrice { get; private set; }
        public Domain.Numerics.Usd RealizedPnl { get; private set; } = Domain.Numerics.Usd.Zero;
        public long OpenedEventNs { get; private set; }

        public Domain.Numerics.Usd Apply(decimal signedQuantity, decimal price, long eventUnixNanoseconds)
        {
            decimal priorQuantity = Quantity;
            if (priorQuantity == 0 || Math.Sign((double)priorQuantity) == Math.Sign((double)signedQuantity))
            {
                decimal priorNotional = Math.Abs(priorQuantity) * AveragePrice;
                decimal newQuantity = priorQuantity + signedQuantity;
                Quantity = newQuantity;
                AveragePrice = newQuantity == 0
                    ? 0
                    : (priorNotional + (Math.Abs(signedQuantity) * price)) / Math.Abs(newQuantity);
                if (priorQuantity == 0 && newQuantity != 0) OpenedEventNs = eventUnixNanoseconds;
                return Domain.Numerics.Usd.Zero;
            }

            decimal closingQuantity = Math.Min(Math.Abs(priorQuantity), Math.Abs(signedQuantity));
            decimal pnlPerUnit = priorQuantity > 0 ? price - AveragePrice : AveragePrice - price;
            Domain.Numerics.Usd realizedDelta = new(closingQuantity * pnlPerUnit);
            RealizedPnl += realizedDelta;
            decimal remainingQuantity = priorQuantity + signedQuantity;
            Quantity = remainingQuantity;
            if (remainingQuantity != 0 && Math.Sign((double)remainingQuantity) != Math.Sign((double)priorQuantity))
            {
                AveragePrice = price;
                OpenedEventNs = eventUnixNanoseconds;
            }

            return realizedDelta;
        }

        public PositionSnapshot Snapshot(decimal? markPrice = null) => new(
            InstrumentSlot,
            Quantity,
            AveragePrice,
            RealizedPnl,
            markPrice is decimal mark ? new Domain.Numerics.Usd((mark - AveragePrice) * Quantity) : Domain.Numerics.Usd.Zero,
            default,
            StrategyId,
            ExitPolicyVersion,
            OpenedEventNs);
    }
}
