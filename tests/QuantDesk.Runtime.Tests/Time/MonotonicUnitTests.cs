using System.Diagnostics;
using QuantDesk.Domain.Numerics;
using QuantDesk.Domain.Strategies;
using QuantDesk.Runtime.Positions;
using QuantDesk.Runtime.Time;

namespace QuantDesk.Runtime.Tests.Time;

/// <summary>
/// A duration converted to monotonic ticks means what the clock says it means.
///
/// The bug these exist for
/// -----------------------
/// Nine places converted a duration with a bare <c>Stopwatch.Frequency</c> -- the exit engine's
/// maximum holding period, both strategy compilers' candidate lifetimes, the decision pipeline's
/// vote validity, execution deadlines, the regime horizon. That is correct only when the timestamps
/// the result is compared against also came from a live Stopwatch.
///
/// Under a virtual clock it is not. <c>Stopwatch.Frequency</c> is 1,000,000,000 on Linux and
/// <c>TimeSpan.TicksPerSecond</c> is 10,000,000, so every one of those durations was a hundred
/// times too long: a five-minute maximum hold became five hundred minutes of virtual time, a
/// replayed session would never have exited on schedule, and every test using the virtual clock to
/// exercise expiry was passing for the wrong reason.
///
/// On Windows the two constants coincide, which is the part that makes this worth a dedicated test
/// rather than a comment. The mistake is invisible on a developer's machine and changes behaviour
/// in the container.
/// </summary>
public sealed class MonotonicUnitTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 3, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheTwoClocksCountInDifferentUnitsAndBothSayWhichTheyMean()
    {
        // Not an incidental difference to be papered over -- it is why the conversion belongs on
        // the clock at all. If these ever agreed everywhere, the bug would still be waiting on the
        // next platform where they do not.
        var live = new LiveRuntimeClock();
        var virtualClock = new VirtualRuntimeClock(Start);

        Assert.Equal(Stopwatch.Frequency, live.MonotonicTicksFor(TimeSpan.FromSeconds(1)));
        Assert.Equal(TimeSpan.TicksPerSecond, virtualClock.MonotonicTicksFor(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void AHoldingPeriodExpiresAfterThatMuchVirtualTime()
    {
        // The regression. Before the fix this needed five hundred virtual minutes to expire on
        // Linux and five on Windows, from identical code.
        var clock = new VirtualRuntimeClock(Start);
        var engine = new ExitEngine(clock);
        PositionManagementPlan plan = PlanHolding(TimeSpan.FromMinutes(5));

        long opened = clock.MonotonicTimestamp;

        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.False(Evaluate(engine, plan, opened, clock).ShouldExit);

        clock.Advance(TimeSpan.FromMinutes(1));
        ExitEvaluation expired = Evaluate(engine, plan, opened, clock);
        Assert.True(expired.ShouldExit);
        Assert.Equal(ExitReason.Expired, expired.Reason);
    }

    [Fact]
    public void TheSameHoldingPeriodExpiresAfterThatMuchLiveTime()
    {
        // The same plan under the live clock, so the fix is a correction of units rather than a
        // change of behaviour for the clock that was already right.
        var clock = new LiveRuntimeClock();
        var engine = new ExitEngine(clock);
        PositionManagementPlan plan = PlanHolding(TimeSpan.FromMinutes(5));

        long opened = 0;
        long fourMinutes = clock.MonotonicTicksFor(TimeSpan.FromMinutes(4));
        long fiveMinutes = clock.MonotonicTicksFor(TimeSpan.FromMinutes(5));

        Assert.False(engine.Evaluate(plan, opened, fourMinutes, new Usd(0), true, true).ShouldExit);
        Assert.True(engine.Evaluate(plan, opened, fiveMinutes, new Usd(0), true, true).ShouldExit);
    }

    [Fact]
    public void ADurationThatWouldOverflowSaturatesRatherThanWrapping()
    {
        // A deadline that wrapped to a negative number would read as already expired, turning an
        // implausibly long TTL into an immediate exit -- a refusal to trade, produced by arithmetic.
        var live = new LiveRuntimeClock();
        var virtualClock = new VirtualRuntimeClock(Start);

        Assert.Equal(long.MaxValue, live.MonotonicTicksFor(TimeSpan.MaxValue));
        Assert.True(virtualClock.MonotonicTicksFor(TimeSpan.MaxValue) > 0);
    }

    [Fact]
    public void ANonPositiveDurationConvertsToNothing()
    {
        // Zero rather than a negative, so "no holding period" reads as no deadline rather than a
        // deadline in the past.
        var live = new LiveRuntimeClock();
        var virtualClock = new VirtualRuntimeClock(Start);

        Assert.Equal(0L, live.MonotonicTicksFor(TimeSpan.Zero));
        Assert.Equal(0L, live.MonotonicTicksFor(TimeSpan.FromMinutes(-5)));
        Assert.Equal(0L, virtualClock.MonotonicTicksFor(TimeSpan.Zero));
        Assert.Equal(0L, virtualClock.MonotonicTicksFor(TimeSpan.FromMinutes(-5)));
    }

    [Fact]
    public void ElapsedMillisecondsAndTheDurationConversionAgreeOnEachClock()
    {
        // The two are inverses. If they disagreed, code could compute a deadline one way and
        // measure the wait the other, and be consistently wrong on one clock only.
        foreach (IRuntimeClock clock in
                 new IRuntimeClock[] { new LiveRuntimeClock(), new VirtualRuntimeClock(Start) })
        {
            long ticks = clock.MonotonicTicksFor(TimeSpan.FromSeconds(90));
            Assert.Equal(90_000d, clock.ElapsedMilliseconds(0, ticks), precision: 3);
        }
    }

    private static ExitEvaluation Evaluate(
        ExitEngine engine, PositionManagementPlan plan, long opened, IRuntimeClock clock) =>
        engine.Evaluate(plan, opened, clock.MonotonicTimestamp, new Usd(0), true, true);

    private static PositionManagementPlan PlanHolding(TimeSpan holding) => new(
        MaximumHoldingPeriod: holding,
        ExitOnThesisInvalidation: false,
        ExitOnRegimeChange: false,
        MaximumAdverseLoss: null,
        MinimumDteToHold: null,
        ExitPolicyVersion: "unit-test-v1");
}
