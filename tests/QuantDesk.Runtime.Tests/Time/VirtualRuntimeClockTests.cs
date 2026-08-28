using QuantDesk.Runtime.Time;

namespace QuantDesk.Runtime.Tests.Time;

public sealed class VirtualRuntimeClockTests
{
    [Fact]
    public void Advance_MovesClockDeterministically()
    {
        DateTimeOffset initial = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);
        var clock = new VirtualRuntimeClock(initial);

        clock.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(initial.AddMinutes(5), clock.UtcNow);
        Assert.Equal(300_000, clock.ElapsedMilliseconds(0, clock.MonotonicTimestamp));
    }

    [Fact]
    public void Advance_RejectsBackwardTime()
    {
        var clock = new VirtualRuntimeClock(DateTimeOffset.UtcNow);

        Assert.Throws<ArgumentOutOfRangeException>(() => clock.Advance(TimeSpan.FromTicks(-1)));
    }
}
