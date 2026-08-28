using System.Diagnostics;
using QuantDesk.Domain.Market;
using QuantDesk.Runtime.Ingestion;
using QuantDesk.Runtime.State;

namespace QuantDesk.Runtime.Tests.Performance;

public sealed class MarketEventThroughputTests
{
    [Fact]
    [Trait("Category", "Performance")]
    public async Task BoundedMarketPipelineProcessesFiftyThousandQuotesWithinBudget()
    {
        const int eventCount = 50_000;
        var channel = new BoundedEventChannel<NormalizedMarketEvent>(1_024);
        var store = new MarketStateStore(1);
        var owner = new MarketStateOwner(store, channel);
        Stopwatch stopwatch = Stopwatch.StartNew();
        Task producer = Task.Run(async () =>
        {
            for (int index = 0; index < eventCount; index++)
            {
                double bid = 100 + (index * 0.001);
                var marketEvent = NormalizedMarketEvent.FromQuote(
                    new QuoteEvent(index + 1, 0, bid, bid + 0.01, 1, 1, index + 1, index + 1, index + 1));
                while (!channel.TryPublish(marketEvent, index + 1)) await Task.Yield();
            }
        });

        int consumed = 0;
        while (!producer.IsCompleted || channel.Depth > 0)
        {
            if (channel.Depth == 0)
            {
                await Task.Yield();
                continue;
            }
            owner.Apply(await channel.ReadAsync(CancellationToken.None));
            consumed++;
        }
        await producer;
        stopwatch.Stop();

        Assert.Equal(eventCount, consumed);
        Assert.Equal(0, channel.Depth);
        Assert.InRange(channel.HighWater, 1, channel.Capacity);
        Assert.Equal(100 + ((eventCount - 1) * 0.001), store.Snapshot(0).Bid, 8);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"Market pipeline exceeded its 10-second CI budget: {stopwatch.Elapsed}.");
    }
}
