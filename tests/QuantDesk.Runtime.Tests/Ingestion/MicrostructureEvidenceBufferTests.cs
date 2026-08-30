using QuantDesk.Domain.Market;
using QuantDesk.Runtime.Ingestion;

namespace QuantDesk.Runtime.Tests.Ingestion;

public sealed class MicrostructureEvidenceBufferTests
{
    [Fact]
    public async Task Buffer_PersistsOnlyOrderBooksAndRecordsOverflowAsAGap()
    {
        var buffer = new MicrostructureEvidenceBuffer(1);
        NormalizedMarketEvent quote = NormalizedMarketEvent.FromQuote(new QuoteEvent(1, 0, 1, 2, 1, 1, 1, 1, 1));
        NormalizedMarketEvent firstBook = OrderBook(2);
        NormalizedMarketEvent secondBook = OrderBook(3);

        Assert.True(buffer.TryPublish(quote, 10));
        Assert.True(buffer.TryPublish(firstBook, 20));
        Assert.False(buffer.TryPublish(secondBook, 30));

        MicrostructureCaptureSnapshot snapshot = buffer.Snapshot();
        Assert.Equal(1, snapshot.Depth);
        Assert.Equal(1, snapshot.GapCount);
        Assert.Equal("evidence_buffer_overflow", snapshot.LastGapReason);
        Assert.Equal(firstBook.OrderBook, await buffer.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public void Buffer_RecordsAnExplicitStreamGap()
    {
        var buffer = new MicrostructureEvidenceBuffer(2);

        buffer.MarkGap("stream_disconnected", 42);

        MicrostructureCaptureSnapshot snapshot = buffer.Snapshot();
        Assert.Equal(1, snapshot.GapCount);
        Assert.Equal(42, snapshot.LastGapMonotonicTicks);
        Assert.Equal("stream_disconnected", snapshot.LastGapReason);
    }

    [Fact]
    public void Buffer_RejectsOutOfOrderEventTimestamps()
    {
        var buffer = new MicrostructureEvidenceBuffer(2);

        Assert.True(buffer.TryPublish(OrderBook(2), 20));
        Assert.False(buffer.TryPublish(OrderBook(1), 30));
        Assert.Equal("non_monotonic_event_time", buffer.Snapshot().LastGapReason);
    }

    private static NormalizedMarketEvent OrderBook(long eventId) =>
        NormalizedMarketEvent.FromOrderBook(new OrderBookEvent(eventId, 0, 100, 101, 3, 4, eventId, eventId, eventId));
}
