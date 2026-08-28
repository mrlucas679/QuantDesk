using QuantDesk.Runtime.Ingestion;

namespace QuantDesk.Runtime.Tests.Ingestion;

public sealed class BoundedEventChannelTests
{
    [Fact]
    public async Task Channel_RejectsOverflowWithoutBlockingProducer()
    {
        var channel = new BoundedEventChannel<int>(2);

        Assert.True(channel.TryPublish(1, 10));
        Assert.True(channel.TryPublish(2, 20));
        Assert.False(channel.TryPublish(3, 30));
        Assert.Equal(2, channel.Depth);
        Assert.Equal(2, channel.HighWater);

        Assert.Equal(1, await channel.ReadAsync(CancellationToken.None));
        Assert.Equal(2, await channel.ReadAsync(CancellationToken.None));
    }
}

