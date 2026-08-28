using QuantDesk.Domain.Runtime;

namespace QuantDesk.Domain.Market;

public static class QuoteValidator
{
    public static ValidationResult Validate(in QuoteEvent quote)
    {
        if (!double.IsFinite(quote.Bid)) return Invalid("NON_FINITE_BID");
        if (!double.IsFinite(quote.Ask)) return Invalid("NON_FINITE_ASK");
        if (!double.IsFinite(quote.BidSize) || quote.BidSize < 0) return Invalid("INVALID_BID_SIZE");
        if (!double.IsFinite(quote.AskSize) || quote.AskSize < 0) return Invalid("INVALID_ASK_SIZE");
        if (quote.Bid < 0) return Invalid("NEGATIVE_BID");
        if (quote.Ask <= 0) return Invalid("NON_POSITIVE_ASK");
        if (quote.Bid > quote.Ask) return Invalid("CROSSED_QUOTE");
        if (quote.InstrumentSlot < 0) return Invalid("UNKNOWN_INSTRUMENT_SLOT");
        if (quote.EventUnixNanoseconds <= 0) return Invalid("INVALID_EVENT_TIMESTAMP");

        return new ValidationResult(true, DataQuality.Healthy, null);
    }

    private static ValidationResult Invalid(string reasonCode) =>
        new(false, DataQuality.Invalid, reasonCode);
}

public static class TradeValidator
{
    public static ValidationResult Validate(in TradeEvent trade)
    {
        if (!double.IsFinite(trade.Price) || trade.Price <= 0) return Invalid("INVALID_TRADE_PRICE");
        if (!double.IsFinite(trade.Size) || trade.Size <= 0) return Invalid("INVALID_TRADE_SIZE");
        if (trade.InstrumentSlot < 0) return Invalid("UNKNOWN_INSTRUMENT_SLOT");
        if (trade.EventUnixNanoseconds <= 0) return Invalid("INVALID_EVENT_TIMESTAMP");

        return new ValidationResult(true, DataQuality.Healthy, null);
    }

    private static ValidationResult Invalid(string reasonCode) =>
        new(false, DataQuality.Invalid, reasonCode);
}

public static class OrderBookValidator
{
    public static ValidationResult Validate(in OrderBookEvent orderBook)
    {
        if (!double.IsFinite(orderBook.BestBid) || orderBook.BestBid < 0) return Invalid("INVALID_BEST_BID");
        if (!double.IsFinite(orderBook.BestAsk) || orderBook.BestAsk <= 0) return Invalid("INVALID_BEST_ASK");
        if (orderBook.BestBid > orderBook.BestAsk) return Invalid("CROSSED_ORDER_BOOK");
        if (!double.IsFinite(orderBook.BidDepth) || orderBook.BidDepth < 0) return Invalid("INVALID_BID_DEPTH");
        if (!double.IsFinite(orderBook.AskDepth) || orderBook.AskDepth < 0) return Invalid("INVALID_ASK_DEPTH");
        if (orderBook.InstrumentSlot < 0) return Invalid("UNKNOWN_INSTRUMENT_SLOT");
        if (orderBook.EventUnixNanoseconds <= 0) return Invalid("INVALID_EVENT_TIMESTAMP");

        return new ValidationResult(true, DataQuality.Healthy, null);
    }

    private static ValidationResult Invalid(string reasonCode) =>
        new(false, DataQuality.Invalid, reasonCode);
}

public static class MarketEventOrdering
{
    public static bool IsLate(long incomingEventNanoseconds, long lastEventNanoseconds) =>
        incomingEventNanoseconds < lastEventNanoseconds;
}
