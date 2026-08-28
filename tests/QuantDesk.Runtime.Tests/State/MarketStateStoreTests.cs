using QuantDesk.Domain.Market;
using QuantDesk.Domain.Runtime;
using QuantDesk.Runtime.State;

namespace QuantDesk.Runtime.Tests.State;

public sealed class MarketStateStoreTests
{
    [Fact]
    public void Apply_CrossedQuoteMarksStateInvalidWithoutPublishingPrices()
    {
        var store = new MarketStateStore(1);
        var crossed = new QuoteEvent(1, 0, 101, 100, 5, 5, 1_000, 10, 1);

        ValidationResult result = store.Apply(crossed);
        InstrumentSnapshot snapshot = store.Snapshot(0);

        Assert.False(result.IsValid);
        Assert.Equal("CROSSED_QUOTE", result.ReasonCode);
        Assert.Equal(DataQuality.Invalid, snapshot.QuoteQuality);
        Assert.Equal(0, snapshot.Bid);
        Assert.Equal(0, snapshot.Ask);
    }

    [Fact]
    public void Apply_LateQuoteCannotOverwriteNewerState()
    {
        var store = new MarketStateStore(1);
        store.Apply(new QuoteEvent(1, 0, 100, 101, 5, 5, 2_000, 20, 2));

        ValidationResult result = store.Apply(new QuoteEvent(2, 0, 90, 91, 5, 5, 1_000, 30, 1));
        InstrumentSnapshot snapshot = store.Snapshot(0);

        Assert.False(result.IsValid);
        Assert.Equal("LATE_QUOTE", result.ReasonCode);
        Assert.Equal(100, snapshot.Bid);
        Assert.Equal(101, snapshot.Ask);
    }

    [Fact]
    public void Apply_TradeUpdatesVwapFromFinancialEvents()
    {
        var store = new MarketStateStore(1);

        store.Apply(new TradeEvent(1, 0, 100, 2, 1_000, 10, 1));
        store.Apply(new TradeEvent(2, 0, 110, 1, 2_000, 20, 2));

        InstrumentSnapshot snapshot = store.Snapshot(0);
        Assert.Equal(310.0 / 3.0, snapshot.Vwap, 10);
        Assert.Equal(3, snapshot.IntervalVolume);
    }

    [Fact]
    public void Apply_OrderBookComputesBoundedImbalance()
    {
        var store = new MarketStateStore(1);

        ValidationResult result = store.Apply(new OrderBookEvent(
            1, 0, 100, 101, 75, 25, 1_000, 10, 1));

        Assert.True(result.IsValid);
        Assert.Equal(0.5, store.Snapshot(0).OrderBookImbalance, 12);
    }
}
