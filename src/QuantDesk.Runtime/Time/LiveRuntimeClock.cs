using System.Diagnostics;

namespace QuantDesk.Runtime.Time;

public sealed class LiveRuntimeClock : IRuntimeClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public long MonotonicTimestamp => Stopwatch.GetTimestamp();

    public double ElapsedMilliseconds(long start, long end) =>
        (end - start) * 1000.0 / Stopwatch.Frequency;

    public long MonotonicTicksFor(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return 0L;
        double ticks = duration.TotalSeconds * Stopwatch.Frequency;
        return ticks >= long.MaxValue ? long.MaxValue : (long)Math.Ceiling(ticks);
    }
}
