using System.Diagnostics;
using System.Collections.Concurrent;
using QuantDesk.Domain.Market;

namespace QuantDesk.Runtime.Ingestion;

/// <summary>
/// Separates raw order-book research evidence from the latency-sensitive market-state path.
/// A rejected write is recorded as a data gap; it never blocks the stream callback.
/// </summary>
public sealed class MicrostructureEvidenceBuffer
{
    private readonly BoundedEventChannel<OrderBookEvent> _events;
    private readonly ConcurrentDictionary<int, long> _lastEventNanoseconds = new();
    private long _gapCount;
    private long _lastGapTicks;
    private string? _lastGapReason;

    public MicrostructureEvidenceBuffer(int capacity)
    {
        _events = new BoundedEventChannel<OrderBookEvent>(capacity);
    }

    public int Depth => _events.Depth;

    public int HighWater => _events.HighWater;

    public int Capacity => _events.Capacity;

    public bool TryPublish(in NormalizedMarketEvent marketEvent, long monotonicTimestamp)
    {
        if (marketEvent.Kind != MarketEventKind.OrderBook)
            return true;

        OrderBookEvent orderBook = marketEvent.OrderBook;
        if (!TryRecordEventTime(orderBook))
        {
            MarkGap("non_monotonic_event_time", monotonicTimestamp);
            return false;
        }

        if (_events.TryPublish(marketEvent.OrderBook, monotonicTimestamp))
            return true;

        MarkGap("evidence_buffer_overflow", monotonicTimestamp);
        return false;
    }

    private bool TryRecordEventTime(OrderBookEvent orderBook)
    {
        if (orderBook.EventUnixNanoseconds <= 0)
            return false;
        if (_lastEventNanoseconds.TryAdd(orderBook.InstrumentSlot, orderBook.EventUnixNanoseconds))
            return true;
        while (true)
        {
            if (!_lastEventNanoseconds.TryGetValue(orderBook.InstrumentSlot, out long previous))
                continue;
            // Alpaca timestamps have millisecond precision, so equal event times are valid.
            if (previous > orderBook.EventUnixNanoseconds)
                return false;
            if (previous == orderBook.EventUnixNanoseconds)
                return true;
            if (_lastEventNanoseconds.TryUpdate(orderBook.InstrumentSlot, orderBook.EventUnixNanoseconds, previous))
                return true;
        }
    }

    public ValueTask<OrderBookEvent> ReadAsync(CancellationToken cancellationToken) =>
        _events.ReadAsync(cancellationToken);

    public void MarkGap(string reasonCode, long monotonicTimestamp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        Volatile.Write(ref _lastGapReason, reasonCode);
        Interlocked.Exchange(ref _lastGapTicks, monotonicTimestamp);
        Interlocked.Increment(ref _gapCount);
    }

    public MicrostructureCaptureSnapshot Snapshot() => new(
        _events.Depth,
        _events.HighWater,
        _events.Capacity,
        Interlocked.Read(ref _gapCount),
        Interlocked.Read(ref _lastGapTicks),
        Volatile.Read(ref _lastGapReason));
}

public sealed record MicrostructureCaptureSnapshot(
    int Depth,
    int HighWater,
    int Capacity,
    long GapCount,
    long LastGapMonotonicTicks,
    string? LastGapReason);
