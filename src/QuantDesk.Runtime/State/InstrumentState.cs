using QuantDesk.Domain.Runtime;

namespace QuantDesk.Runtime.State;

public struct QuoteState
{
    public double Bid;
    public double Ask;
    public double BidSize;
    public double AskSize;
    public long EventUnixNanoseconds;
    public long ReceiveMonotonicTicks;
    public long Version;

    public readonly double Mid => (Bid + Ask) * 0.5;

    public readonly double Spread => Ask - Bid;

    public readonly double RelativeSpread => Mid <= 0 ? double.PositiveInfinity : Spread / Mid;
}

public struct OrderBookState
{
    public double BestBid;
    public double BestAsk;
    public double BidDepth;
    public double AskDepth;
    public long EventUnixNanoseconds;
    public long Version;

    public readonly double Imbalance
    {
        get
        {
            double denominator = BidDepth + AskDepth;
            return denominator <= 0 ? 0 : (BidDepth - AskDepth) / denominator;
        }
    }
}

internal sealed class InstrumentState
{
    public QuoteState Quote;
    public OrderBookState OrderBook;
    public double LastTrade;
    public double LastTradeSize;
    public double IntervalVolume;
    public double SessionOrRollingVolume;
    public double VwapNumerator;
    public double VwapDenominator;
    public long LastTradeEventNs;
    public long LastReceiveTicks;
    public long Version;
    public DataQuality QuoteQuality = DataQuality.Disconnected;
    public DataQuality TradeQuality = DataQuality.Disconnected;
    public DataQuality OrderBookQuality = DataQuality.Disconnected;
}

public readonly record struct InstrumentSnapshot(
    int InstrumentSlot,
    long StateVersion,
    double Bid,
    double Ask,
    double Mid,
    double RelativeSpread,
    double LastTrade,
    double Vwap,
    double IntervalVolume,
    double OrderBookImbalance,
    long QuoteEventNs,
    long TradeEventNs,
    long OrderBookEventNs,
    long LastReceiveTicks,
    DataQuality QuoteQuality,
    DataQuality TradeQuality,
    DataQuality OrderBookQuality);

