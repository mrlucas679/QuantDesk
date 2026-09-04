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

    /// <summary>
    /// Virtual monotonic time counts in TimeSpan ticks, which is what <see cref="Advance"/> adds.
    ///
    /// Ten million to the second, against the live clock's Stopwatch frequency of a thousand
    /// million on Linux. Any code converting a duration without asking the clock gets one of those
    /// two answers and has no way to know which it needed.
    /// </summary>
    public long MonotonicTicksFor(TimeSpan duration) =>
        duration <= TimeSpan.Zero ? 0L : duration.Ticks;

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
