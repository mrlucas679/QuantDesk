namespace QuantDesk.Runtime.Time;

public sealed class VirtualRuntimeClock(DateTimeOffset initialTime) : IRuntimeClock
{
    private readonly Lock _gate = new();
    private DateTimeOffset _utcNow = initialTime.ToUniversalTime();
    private long _monotonicTimestamp;

    public DateTimeOffset UtcNow
    {
        get
        {
            lock (_gate)
            {
                return _utcNow;
            }
        }
    }

    public long MonotonicTimestamp
    {
        get
        {
            lock (_gate)
            {
                return _monotonicTimestamp;
            }
        }
    }

    public double ElapsedMilliseconds(long start, long end) =>
        TimeSpan.FromTicks(end - start).TotalMilliseconds;

    public void Advance(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Virtual time cannot move backwards.");
        }

        lock (_gate)
        {
            _utcNow = _utcNow.Add(duration);
            _monotonicTimestamp += duration.Ticks;
        }
    }
}
