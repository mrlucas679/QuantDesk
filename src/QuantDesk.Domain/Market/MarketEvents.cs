using QuantDesk.Domain.Runtime;

namespace QuantDesk.Domain.Market;

public readonly record struct QuoteEvent(
    long EventId,
    int InstrumentSlot,
    double Bid,
    double Ask,
    double BidSize,
    double AskSize,
    long EventUnixNanoseconds,
    long ReceiveMonotonicTicks,
    long SourceSequence);

public readonly record struct TradeEvent(
    long EventId,
    int InstrumentSlot,
    double Price,
    double Size,
    long EventUnixNanoseconds,
    long ReceiveMonotonicTicks,
    long SourceSequence);

public readonly record struct OrderBookEvent(
    long EventId,
    int InstrumentSlot,
    double BestBid,
    double BestAsk,
    double BidDepth,
    double AskDepth,
    long EventUnixNanoseconds,
    long ReceiveMonotonicTicks,
    long SourceSequence);

public readonly record struct ValidationResult(
    bool IsValid,
    DataQuality Quality,
    string? ReasonCode);

public enum MarketEventKind
{
    Quote,
    Trade,
    OrderBook
}

public readonly record struct NormalizedMarketEvent
{
    private NormalizedMarketEvent(
        MarketEventKind kind,
        QuoteEvent quote,
        TradeEvent trade,
        OrderBookEvent orderBook)
    {
        Kind = kind;
        Quote = quote;
        Trade = trade;
        OrderBook = orderBook;
    }

    public MarketEventKind Kind { get; }

    public QuoteEvent Quote { get; }

    public TradeEvent Trade { get; }

    public OrderBookEvent OrderBook { get; }

    public static NormalizedMarketEvent FromQuote(in QuoteEvent quote) =>
        new(MarketEventKind.Quote, quote, default, default);

    public static NormalizedMarketEvent FromTrade(in TradeEvent trade) =>
        new(MarketEventKind.Trade, default, trade, default);

    public static NormalizedMarketEvent FromOrderBook(in OrderBookEvent orderBook) =>
        new(MarketEventKind.OrderBook, default, default, orderBook);
}
