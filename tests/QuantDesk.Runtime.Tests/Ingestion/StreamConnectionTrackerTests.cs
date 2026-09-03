using QuantDesk.Runtime.Ingestion;
using QuantDesk.Runtime.Time;

namespace QuantDesk.Runtime.Tests.Ingestion;

/// <summary>
/// Counting reconnections, which gate R12 requires and nothing measured before.
///
/// The failure being watched for is a socket that drops and redials every few seconds. Every health
/// check stays green -- each individual connection succeeds, data arrives in bursts, readiness
/// flickers back to healthy between drops -- while the market state is destroyed, because this
/// venue publishes no sequence number and each reconnect loses an unknown number of updates.
///
/// So the measure is not whether the stream is up. It is how often it has had to come back up.
/// </summary>
public sealed class StreamConnectionTrackerTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AStreamThatHasNeverConnectedIsNotLeaking()
    {
        var tracker = new StreamConnectionTracker(new VirtualRuntimeClock(Start));

        Assert.True(tracker.NoReconnectLeak());
        Assert.Empty(tracker.Summarise());
    }

    [Fact]
    public void AnOccasionalDropIsOrdinaryOperation()
    {
        var clock = new VirtualRuntimeClock(Start);
        var tracker = new StreamConnectionTracker(clock);

        tracker.Record("crypto", connected: true, clock.UtcNow);
        tracker.Record("crypto", connected: false, clock.UtcNow);
        tracker.Record("crypto", connected: true, clock.UtcNow);

        Assert.True(tracker.NoReconnectLeak());
    }

    [Fact]
    public void AFlappingStreamIsReportedEvenThoughEveryConnectionSucceeded()
    {
        var clock = new VirtualRuntimeClock(Start);
        var tracker = new StreamConnectionTracker(clock);

        for (int cycle = 0; cycle <= StreamConnectionTracker.MaximumReconnectsInWindow; cycle++)
        {
            tracker.Record("crypto", connected: true, clock.UtcNow);
            tracker.Record("crypto", connected: false, clock.UtcNow);
            clock.Advance(TimeSpan.FromSeconds(30));
        }

        Assert.False(tracker.NoReconnectLeak());
    }

    [Fact]
    public void DropsOlderThanTheWindowStopCountingAgainstTheStream()
    {
        // A network blip yesterday must not hold the gate closed forever, or the measure stops
        // describing the system that is running now.
        var clock = new VirtualRuntimeClock(Start);
        var tracker = new StreamConnectionTracker(clock);

        for (int cycle = 0; cycle <= StreamConnectionTracker.MaximumReconnectsInWindow; cycle++)
        {
            tracker.Record("crypto", connected: true, clock.UtcNow);
            tracker.Record("crypto", connected: false, clock.UtcNow);
        }

        Assert.False(tracker.NoReconnectLeak());

        clock.Advance(StreamConnectionTracker.Window + TimeSpan.FromMinutes(1));

        Assert.True(tracker.NoReconnectLeak());
    }

    [Fact]
    public void RepeatingTheSameStateIsNotATransition()
    {
        // A stream reporting "still connected" on a timer would otherwise inflate the counts and
        // bury a genuine flap in the noise.
        var clock = new VirtualRuntimeClock(Start);
        var tracker = new StreamConnectionTracker(clock);

        tracker.Record("crypto", connected: true, clock.UtcNow);
        tracker.Record("crypto", connected: true, clock.UtcNow);
        tracker.Record("crypto", connected: true, clock.UtcNow);

        StreamConnectionSummary summary = Assert.Single(tracker.Summarise());
        Assert.Equal(1, summary.Connects);
        Assert.Equal(0, summary.Disconnects);
    }

    [Fact]
    public void EachStreamIsCountedSeparately()
    {
        // Market data and trade updates are different sockets, and one flapping must not be
        // averaged away by the other staying up.
        var clock = new VirtualRuntimeClock(Start);
        var tracker = new StreamConnectionTracker(clock);

        tracker.Record("trade-updates", connected: true, clock.UtcNow);
        for (int cycle = 0; cycle <= StreamConnectionTracker.MaximumReconnectsInWindow; cycle++)
        {
            tracker.Record("crypto", connected: true, clock.UtcNow);
            tracker.Record("crypto", connected: false, clock.UtcNow);
        }

        Assert.Equal(2, tracker.Summarise().Count);
        Assert.False(tracker.NoReconnectLeak());
        Assert.Equal(
            0,
            tracker.Summarise().Single(item => item.Name == "trade-updates").ReconnectsInWindow);
    }
}
