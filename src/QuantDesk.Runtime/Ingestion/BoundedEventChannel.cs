using System.Threading.Channels;
using System.Collections.Concurrent;

namespace QuantDesk.Runtime.Ingestion;

public sealed class BoundedEventChannel<T>
{
    private readonly Channel<Entry> _channel;
    private readonly ConcurrentQueue<long> _timestamps = new();
    private int _depth;
    private int _highWater;
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

    public bool TryPublish(T value, long monotonicTimestamp)
    {
        if (!_channel.Writer.TryWrite(new Entry(value, monotonicTimestamp))) return false;

        _timestamps.Enqueue(monotonicTimestamp);
        int depth = Interlocked.Increment(ref _depth);
        UpdateHighWater(depth);
        Interlocked.CompareExchange(ref _oldestEnqueuedTicks, monotonicTimestamp, 0);
        return true;
    }

    public async ValueTask<T> ReadAsync(CancellationToken cancellationToken)
    {
        Entry entry = await _channel.Reader.ReadAsync(cancellationToken);
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
