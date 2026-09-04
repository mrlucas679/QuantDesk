using QuantDesk.Domain.Market;
using QuantDesk.Domain.Runtime;

namespace QuantDesk.Runtime.State;

public sealed class MarketStateStore
{
    private readonly InstrumentState[] _states;

    public MarketStateStore(int instrumentCapacity)
    {
        if (instrumentCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(instrumentCapacity));
        }

        _states = new InstrumentState[instrumentCapacity];
        for (int index = 0; index < _states.Length; index++)
        {
            _states[index] = new InstrumentState();
        }
    }

    public ValidationResult Apply(in QuoteEvent quote)
    {
        ValidationResult validation = QuoteValidator.Validate(quote);
        InstrumentState state = GetState(quote.InstrumentSlot);

        if (!validation.IsValid)
        {
            state.QuoteQuality = validation.Quality;
            state.Version++;
            return validation;
        }

        if (MarketEventOrdering.IsLate(quote.EventUnixNanoseconds, state.Quote.EventUnixNanoseconds))
        {
            state.QuoteQuality = DataQuality.Stale;
            return new ValidationResult(false, DataQuality.Stale, "LATE_QUOTE");
        }

        state.Quote = new QuoteState
        {
            Bid = quote.Bid,
            Ask = quote.Ask,
            BidSize = quote.BidSize,
            AskSize = quote.AskSize,
            EventUnixNanoseconds = quote.EventUnixNanoseconds,
            ReceiveMonotonicTicks = quote.ReceiveMonotonicTicks,
            Version = state.Quote.Version + 1
        };
        state.QuoteQuality = DataQuality.Healthy;
        state.LastReceiveTicks = quote.ReceiveMonotonicTicks;
        state.Version++;
        return validation;
    }

    public ValidationResult Apply(in TradeEvent trade)
    {
        ValidationResult validation = TradeValidator.Validate(trade);
        InstrumentState state = GetState(trade.InstrumentSlot);

        if (!validation.IsValid)
        {
            state.TradeQuality = validation.Quality;
            state.Version++;
            return validation;
        }

        if (MarketEventOrdering.IsLate(trade.EventUnixNanoseconds, state.LastTradeEventNs))
        {
            state.TradeQuality = DataQuality.Stale;
            return new ValidationResult(false, DataQuality.Stale, "LATE_TRADE");
        }

        state.LastTrade = trade.Price;
        state.LastTradeSize = trade.Size;
        state.IntervalVolume += trade.Size;
        state.SessionOrRollingVolume += trade.Size;
        state.VwapNumerator += trade.Price * trade.Size;
        state.VwapDenominator += trade.Size;
        state.LastTradeEventNs = trade.EventUnixNanoseconds;
        state.LastReceiveTicks = trade.ReceiveMonotonicTicks;
        state.TradeQuality = DataQuality.Healthy;
        state.Version++;
        return validation;
    }

    public ValidationResult Apply(in OrderBookEvent orderBook)
    {
        ValidationResult validation = OrderBookValidator.Validate(orderBook);
        InstrumentState state = GetState(orderBook.InstrumentSlot);

        if (!validation.IsValid)
        {
            state.OrderBookQuality = validation.Quality;
            state.Version++;
            return validation;
        }

        if (MarketEventOrdering.IsLate(orderBook.EventUnixNanoseconds, state.OrderBook.EventUnixNanoseconds))
        {
            state.OrderBookQuality = DataQuality.Stale;
            return new ValidationResult(false, DataQuality.Stale, "LATE_ORDER_BOOK");
        }

        state.OrderBook = new OrderBookState
        {
            BestBid = orderBook.BestBid,
            BestAsk = orderBook.BestAsk,
            BidDepth = orderBook.BidDepth,
            AskDepth = orderBook.AskDepth,
            EventUnixNanoseconds = orderBook.EventUnixNanoseconds,
            Version = state.OrderBook.Version + 1
        };
        state.OrderBookQuality = DataQuality.Healthy;
        state.LastReceiveTicks = orderBook.ReceiveMonotonicTicks;
        state.Version++;
        return validation;
    }

    /// <summary>
    /// Marks every instrument's evidence as gapped, because the feed stopped and started again.
    ///
    /// This venue publishes no usable sequence number -- crypto quotes and order books carry a
    /// constant zero and trades carry what looks like a hashed id -- so a dropped message is
    /// undetectable from the messages themselves. Measured over a live session: 16,396 order-book
    /// events on one instrument, every consecutive sequence delta zero.
    ///
    /// A disconnection is therefore the only evidence of loss the feed offers. An unknown number of
    /// updates happened while the socket was down, so on reconnect the book on hand is not the
    /// venue's book, and it is not merely old -- it is wrong by an unknown amount, which is worse,
    /// because staleness at least shows up in a timestamp. Until a fresh event arrives for an
    /// instrument, nothing may trade on it.
    ///
    /// Without this the snapshots kept reporting Healthy across a reconnect, carrying the
    /// pre-disconnect book forward as though nothing had happened.
    /// </summary>
    public void MarkStreamInterrupted()
    {
        foreach (InstrumentState state in _states)
        {
            state.QuoteQuality = DataQuality.GapDetected;
            state.TradeQuality = DataQuality.GapDetected;
            state.OrderBookQuality = DataQuality.GapDetected;
            state.Version++;
        }
    }

    public InstrumentSnapshot Snapshot(int instrumentSlot)
    {
        InstrumentState state = GetState(instrumentSlot);
        double vwap = state.VwapDenominator <= 0 ? double.NaN : state.VwapNumerator / state.VwapDenominator;

        return new InstrumentSnapshot(
            instrumentSlot,
            state.Version,
            state.Quote.Bid,
            state.Quote.Ask,
            state.Quote.Mid,
            state.Quote.RelativeSpread,
            state.LastTrade,
            vwap,
            state.IntervalVolume,
            state.OrderBook.Imbalance,
            state.Quote.EventUnixNanoseconds,
            state.LastTradeEventNs,
            state.OrderBook.EventUnixNanoseconds,
            state.LastReceiveTicks,
            state.QuoteQuality,
            state.TradeQuality,
            state.OrderBookQuality);
    }

    private InstrumentState GetState(int slot)
    {
        if ((uint)slot >= (uint)_states.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(slot), "Instrument slot is outside the configured universe.");
        }

        return _states[slot];
    }
}
