namespace QuantDesk.Runtime.Time;

public interface IRuntimeClock
{
    DateTimeOffset UtcNow { get; }

    long MonotonicTimestamp { get; }

    double ElapsedMilliseconds(long start, long end);
}
