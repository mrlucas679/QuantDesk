using QuantDesk.Domain.Market;
using QuantDesk.Runtime.Ingestion;
using QuantDesk.Runtime.State;

namespace QuantDesk.Runtime.Tests.State;

public sealed class MarketStateOwnerTests
{
    [Fact]
    public void Apply_DispatchesNormalizedEventsAndInvalidDataCannotReplaceState()
    {
        var store = new MarketStateStore(1);
        var channel = new BoundedEventChannel<NormalizedMarketEvent>(2);
        var owner = new MarketStateOwner(store, channel);
        ValidationResult valid = owner.Apply(NormalizedMarketEvent.FromQuote(
            new QuoteEvent(1, 0, 100, 101, 1, 1, 1_000, 10, 1)));
        ValidationResult invalid = owner.Apply(NormalizedMarketEvent.FromQuote(
            new QuoteEvent(2, 0, 102, 101, 1, 1, 2_000, 20, 2)));

        Assert.True(valid.IsValid);
        Assert.False(invalid.IsValid);
        Assert.Equal(100, store.Snapshot(0).Bid);
    }
}
