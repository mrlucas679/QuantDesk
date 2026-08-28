using System.Diagnostics;

namespace QuantDesk.Runtime.Time;

public sealed class LiveRuntimeClock : IRuntimeClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public long MonotonicTimestamp => Stopwatch.GetTimestamp();

    public double ElapsedMilliseconds(long start, long end) =>
        (end - start) * 1000.0 / Stopwatch.Frequency;
}
