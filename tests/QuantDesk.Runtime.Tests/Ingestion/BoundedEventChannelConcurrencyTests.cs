using QuantDesk.Runtime.Ingestion;

namespace QuantDesk.Runtime.Tests.Ingestion;

public sealed class BoundedEventChannelConcurrencyTests
{
    [Fact]
    public async Task ConcurrentProducersAndConsumerKeepTimestampBookkeepingConsistent()
    {
        const int producerCount = 12;
        const int valuesPerProducer = 1_000;
        var channel = new BoundedEventChannel<int>(64);
        int accepted = 0;
        Task producers = Task.WhenAll(Enumerable.Range(0, producerCount).Select(async producer =>
        {
            for (int index = 0; index < valuesPerProducer; index++)
            {
                int value = producer * valuesPerProducer + index;
                while (!channel.TryPublish(value, value + 1)) await Task.Yield();
                Interlocked.Increment(ref accepted);
            }
        }));
        var received = new HashSet<int>();
        while (!producers.IsCompleted || channel.Depth > 0)
        {
            if (channel.Depth == 0)
            {
                await Task.Yield();
                continue;
            }
            received.Add(await channel.ReadAsync(CancellationToken.None));
        }
        await producers;

        Assert.Equal(producerCount * valuesPerProducer, accepted);
        Assert.Equal(accepted, received.Count);
        Assert.Equal(0, channel.Depth);
        Assert.InRange(channel.HighWater, 1, channel.Capacity);
    }

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
