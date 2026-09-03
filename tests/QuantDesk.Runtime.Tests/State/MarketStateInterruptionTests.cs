using QuantDesk.Domain.Market;
using QuantDesk.Domain.Runtime;
using QuantDesk.Runtime.State;

namespace QuantDesk.Runtime.Tests.State;

/// <summary>
/// What happens to the book when the feed drops and comes back.
///
/// This venue publishes no usable sequence number. Measured over a live session: every consecutive
/// order-book sequence delta was zero across 16,396 events on one instrument, and trades carried
/// what look like hashed ids rather than a sequence. A dropped message therefore cannot be detected
/// from the messages themselves, and gap detection by sequence number is not available here.
///
/// A disconnection is the only evidence of loss the feed offers. After one, the book on hand is not
/// merely old -- it is wrong by an unknown amount, which is worse, because staleness at least shows
/// up in a timestamp. Until this change the snapshots kept reporting Healthy straight across a
/// reconnect, carrying the pre-disconnect book forward as though nothing had happened.
/// </summary>
public sealed class MarketStateInterruptionTests
{
    [Fact]
    public void AnInterruptedStreamMarksEveryInstrumentGapped()
    {
        MarketStateStore store = Populated();

        store.MarkStreamInterrupted();

        Assert.Equal(DataQuality.GapDetected, store.Snapshot(0).QuoteQuality);
        Assert.Equal(DataQuality.GapDetected, store.Snapshot(1).QuoteQuality);
        Assert.Equal(DataQuality.GapDetected, store.Snapshot(0).OrderBookQuality);
    }

    [Fact]
    public void APreviouslyHealthyBookDoesNotSurviveTheReconnectAsHealthy()
    {
        // The regression. Every downstream actionability check reads this quality, so a book that
        // stays Healthy across a reconnect is a book the system will trade on having missed an
        // unknown number of updates.
        MarketStateStore store = Populated();
        Assert.Equal(DataQuality.Healthy, store.Snapshot(0).QuoteQuality);

        store.MarkStreamInterrupted();

        Assert.NotEqual(DataQuality.Healthy, store.Snapshot(0).QuoteQuality);
    }

    [Fact]
    public void AFreshQuotePerInstrumentClearsTheGapForThatInstrumentOnly()
    {
        // Recovery is per instrument because the evidence is per instrument. One symbol quoting
        // again says nothing about a symbol that has not.
        MarketStateStore store = Populated();
        store.MarkStreamInterrupted();

        store.Apply(Quote(slot: 0, bid: 30_100d, ask: 30_110d, eventNs: 9_000L));

        Assert.Equal(DataQuality.Healthy, store.Snapshot(0).QuoteQuality);
        Assert.Equal(DataQuality.GapDetected, store.Snapshot(1).QuoteQuality);
    }

    [Fact]
    public void TheStateVersionMovesSoDownstreamCausalityChecksSeeTheChange()
    {
        // Forecasts are validated against the state version they were computed from. A gap that did
        // not move the version would leave a forecast built on the pre-disconnect book still
        // reading as causal.
        MarketStateStore store = Populated();
        long before = store.Snapshot(0).StateVersion;

        store.MarkStreamInterrupted();

        Assert.True(store.Snapshot(0).StateVersion > before);
    }

    // ------------------------------------------------------------------------------- fixtures

    private static MarketStateStore Populated()
    {
        var store = new MarketStateStore(instrumentCapacity: 4);
        store.Apply(Quote(slot: 0, bid: 30_000d, ask: 30_010d, eventNs: 1_000L));
        store.Apply(Quote(slot: 1, bid: 2_000d, ask: 2_001d, eventNs: 1_000L));
        store.Apply(new OrderBookEvent(
            EventId: 1, InstrumentSlot: 0, BestBid: 30_000d, BestAsk: 30_010d,
            BidDepth: 5d, AskDepth: 5d, EventUnixNanoseconds: 1_000L,
            ReceiveMonotonicTicks: 0L, SourceSequence: 0));
        return store;
    }

    private static QuoteEvent Quote(int slot, double bid, double ask, long eventNs) => new(
        EventId: 1, InstrumentSlot: slot, Bid: bid, Ask: ask, BidSize: 1d, AskSize: 1d,
        EventUnixNanoseconds: eventNs, ReceiveMonotonicTicks: 0L, SourceSequence: 0);
}
