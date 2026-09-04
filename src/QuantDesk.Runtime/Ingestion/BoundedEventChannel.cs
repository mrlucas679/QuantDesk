using System.Threading.Channels;
using System.Collections.Concurrent;

namespace QuantDesk.Runtime.Ingestion;

public sealed class BoundedEventChannel<T>
{
    private readonly Channel<Entry> _channel;
    private readonly ConcurrentQueue<long> _timestamps = new();
    private readonly object _bookkeepingGate = new();
    private int _depth;
    private int _highWater;
    private long _rejected;
    private long _oldestEnqueuedTicks;

    public BoundedEventChannel(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        Capacity = capacity;
        _channel = Channel.CreateBounded<Entry>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
    }

    public int Capacity { get; }

    public int Depth => Volatile.Read(ref _depth);

    public int HighWater => Volatile.Read(ref _highWater);

    /// <summary>
    /// How many events this channel refused because it was full.
    ///
    /// Gate R12 asks whether queues are bounded, and a capacity alone does not answer it: every
    /// channel here is bounded by construction, so the property that actually matters is whether
    /// the bound was ever reached. A non-zero count means the runtime dropped market data it had
    /// already received, which is a different and worse thing than being slow.
    /// </summary>
    public long Rejected => Interlocked.Read(ref _rejected);

    /// <summary>True when the bound has never been reached.</summary>
    public bool WithinBounds => Rejected == 0;

    public bool TryPublish(T value, long monotonicTimestamp)
    {
        lock (_bookkeepingGate)
        {
            if (!_channel.Writer.TryWrite(new Entry(value, monotonicTimestamp)))
            {
                Interlocked.Increment(ref _rejected);
                return false;
            }

            _timestamps.Enqueue(monotonicTimestamp);
            int depth = Interlocked.Increment(ref _depth);
            UpdateHighWater(depth);
            Interlocked.CompareExchange(ref _oldestEnqueuedTicks, monotonicTimestamp, 0);
            return true;
        }
    }

    public async ValueTask<T> ReadAsync(CancellationToken cancellationToken)
    {
        while (await _channel.Reader.WaitToReadAsync(cancellationToken))
        {
            lock (_bookkeepingGate)
            {
                if (!_channel.Reader.TryRead(out Entry entry)) continue;
                if (!_timestamps.TryDequeue(out _))
                {
                    throw new InvalidOperationException("Channel depth and timestamp tracking diverged.");
                }

                int depth = Interlocked.Decrement(ref _depth);
                if (depth == 0)
                {
                    Interlocked.Exchange(ref _oldestEnqueuedTicks, 0);
                }
                else if (_timestamps.TryPeek(out long oldest))
                {
                    Interlocked.Exchange(ref _oldestEnqueuedTicks, oldest);
                }
                return entry.Value;
            }
        }
        throw new ChannelClosedException();
    }

    public double OldestMessageAgeMilliseconds(long nowMonotonicTimestamp, double timestampFrequency)
    {
        long oldest = Volatile.Read(ref _oldestEnqueuedTicks);
        if (oldest == 0 || timestampFrequency <= 0) return 0;

        return Math.Max(0, (nowMonotonicTimestamp - oldest) * 1000.0 / timestampFrequency);
    }

    private void UpdateHighWater(int depth)
    {
        int observed = Volatile.Read(ref _highWater);
        while (depth > observed)
        {
            int original = Interlocked.CompareExchange(ref _highWater, depth, observed);
            if (original == observed) return;
            observed = original;
        }
    }

    private readonly record struct Entry(T Value, long EnqueuedTimestamp);
}
