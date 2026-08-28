using QuantDesk.Runtime.Ingestion;

namespace QuantDesk.Runtime.Tests.Ingestion;

public sealed class BoundedEventChannelConcurrencyTests
{
    [Fact]
    public async Task ConcurrentWritersNeverExceedCapacity()
    {
        var channel = new BoundedEventChannel<int>(32);
        int accepted = 0;
        await Task.WhenAll(Enumerable.Range(0, 8).Select(async worker =>
        {
            await Task.Yield();
            for (int i = 0; i < 100; i++)
                if (channel.TryPublish(worker * 100 + i, i + 1)) Interlocked.Increment(ref accepted);
        }));
        Assert.InRange(channel.HighWater, 1, channel.Capacity);
        Assert.Equal(channel.Depth, accepted);
    }
}
