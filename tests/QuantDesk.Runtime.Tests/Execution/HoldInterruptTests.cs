using QuantDesk.Domain.Execution;
using QuantDesk.Runtime.Execution;
using QuantDesk.Runtime.Persistence;
using QuantDesk.Runtime.Time;

namespace QuantDesk.Runtime.Tests.Execution;

public sealed class AdverseLossHoldInterruptTests
{
    [Fact]
    public void APositionPastItsDefinedMaximumLossExits()
    {
        // The gap this closes. DefinedMaximumLoss was computed, persisted, and used to size the
        // capital reservation -- and never compared against anything. The only thing that ended a
        // hold was the clock, so a position could lose several multiples of its stated maximum and
        // keep running to its timer.
        var interrupt = new AdverseLossHoldInterrupt(new StubMarker(90m));

        HoldInterrupt result = interrupt.Evaluate(Held(entryPrice: 100m, quantity: 1m, maximumLoss: 5m));

        Assert.True(result.ShouldExitNow);
        Assert.Contains("AdverseLossBreached", result.Reason);
    }

    [Fact]
    public void APositionInsideItsBudgetKeepsHolding()
    {
        var interrupt = new AdverseLossHoldInterrupt(new StubMarker(98m));

        Assert.False(interrupt.Evaluate(Held(100m, 1m, 5m)).ShouldExitNow);
    }

    [Fact]
    public void ExactlyAtTheLimitExits()
    {
        // The boundary belongs to the stop. A maximum loss the position is allowed to sit exactly
        // on is not a maximum.
        var interrupt = new AdverseLossHoldInterrupt(new StubMarker(95m));

        Assert.True(interrupt.Evaluate(Held(100m, 1m, 5m)).ShouldExitNow);
    }

    [Fact]
    public void AProfitablePositionNeverTriggersTheStop()
    {
        var interrupt = new AdverseLossHoldInterrupt(new StubMarker(120m));

        Assert.False(interrupt.Evaluate(Held(100m, 1m, 5m)).ShouldExitNow);
    }

    [Fact]
    public void NoQuoteMeansNoStopRatherThanAnImmediateExit()
    {
        // Firing on absent data would liquidate during a feed outage -- the moment the account is
        // least able to judge the price it would actually get. The scheduled exit still bounds it.
        var interrupt = new AdverseLossHoldInterrupt(new StubMarker(null));

        Assert.False(interrupt.Evaluate(Held(100m, 1m, 5m)).ShouldExitNow);
    }

    [Fact]
    public void AnUnfilledRecordCannotBreachAStop()
    {
        var interrupt = new AdverseLossHoldInterrupt(new StubMarker(10m));

        Assert.False(interrupt.Evaluate(Held(100m, 0m, 5m)).ShouldExitNow);
    }

    private static HeldPosition Held(decimal entryPrice, decimal quantity, decimal maximumLoss) =>
        new("exec", "BTC/USD", quantity, entryPrice, maximumLoss, null, EarliestLegExpiry: null);

    private sealed class StubMarker(decimal? mid) : IHeldPositionMarker
    {
        public decimal? CurrentMid(string symbol) => mid;
    }
}

public sealed class CompositeHoldInterruptTests
{
    [Fact]
    public void TheFirstInterruptToFireNamesTheExit()
    {
        var composite = new CompositeHoldInterrupt(
            new Always(false, null), new Always(true, "first"), new Always(true, "second"));

        HoldInterrupt result = composite.Evaluate(Record());

        Assert.True(result.ShouldExitNow);
        Assert.Equal("first", result.Reason);
    }

    [Fact]
    public void NoInterruptsMeansTheTimerStillGoverns()
    {
        Assert.False(new CompositeHoldInterrupt().Evaluate(Record()).ShouldExitNow);
    }

    private static HeldPosition Record() =>
        new("exec", "BTC/USD", 1m, 100m, 5m, null, EarliestLegExpiry: null);

    private sealed class Always(bool exit, string? reason) : IHoldInterrupt
    {
        public HoldInterrupt Evaluate(in HeldPosition position) => new(exit, reason);
    }
}

public sealed class ExpiryHoldInterruptTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void APositionsOwnMinimumWinsOverTheLanesFloor()
    {
        // MinimumDteToHold existed on the management plan and was passed as null by every compiler
        // and read by nothing -- a rule stated in the domain and absent from the system. A position
        // that asks for ten days gets ten, even where the lane's floor is two, because a wide spread
        // and a tight one do not become dangerous at the same distance from expiry.
        var interrupt = new ExpiryHoldInterrupt(new FixedClock(Now), minimumDaysToExpiry: 2);

        HoldInterrupt result = interrupt.Evaluate(Option(Now.AddDays(6), minimumDays: 10));

        Assert.True(result.ShouldExitNow);
        Assert.Contains("<=10d", result.Reason);
    }

    [Fact]
    public void TheLanesFloorAppliesWhenThePositionStatedNoMinimum()
    {
        var interrupt = new ExpiryHoldInterrupt(new FixedClock(Now), minimumDaysToExpiry: 2);

        Assert.False(interrupt.Evaluate(Option(Now.AddDays(6), minimumDays: null)).ShouldExitNow);
        Assert.True(interrupt.Evaluate(Option(Now.AddDays(1), minimumDays: null)).ShouldExitNow);
    }

    [Fact]
    public void SpotHasNoExpiryAndIsNeverClosedForOne()
    {
        var interrupt = new ExpiryHoldInterrupt(new FixedClock(Now), minimumDaysToExpiry: 2);

        HeldPosition spot = new("exec", "BTC/USD", 1m, 100m, 5m, null, EarliestLegExpiry: null);

        Assert.False(interrupt.Evaluate(spot).ShouldExitNow);
    }

    private static HeldPosition Option(DateTimeOffset expiry, int? minimumDays) =>
        new("exec", "SPY", 1m, 1.25m, 100m, null, expiry, minimumDays);

    private sealed class FixedClock(DateTimeOffset now) : IRuntimeClock
    {
        public DateTimeOffset UtcNow => now;

        public long MonotonicTimestamp => now.Ticks;

        public double ElapsedMilliseconds(long fromTicks, long toTicks) =>
            TimeSpan.FromTicks(toTicks - fromTicks).TotalMilliseconds;
    }
}

public sealed class PositionOwnershipTests
{
    [Fact]
    public void ARefreshedForecastFromTheSameArtifactStillAuthorisesThePosition()
    {
        // Publishing a new forecast each horizon is the artifact working normally. Treating that as
        // a change of licence would close every position at the first refresh.
        PositionOwnership ownership = Bound();

        Assert.True(ownership.Matches("artifact-1", "v3", "hash-abc"));
    }

    [Fact]
    public void ADifferentArtifactDoesNotAuthoriseAPositionItNeverOpened()
    {
        Assert.False(Bound().Matches("artifact-2", "v3", "hash-abc"));
    }

    [Fact]
    public void TheSameArtifactRepublishedWithDifferentContentIsADifferentLicence()
    {
        // The case an ID comparison alone would miss: same name, changed model.
        Assert.False(Bound().Matches("artifact-1", "v3", "hash-changed"));
    }

    [Fact]
    public void ADifferentModelVersionIsADifferentLicence()
    {
        Assert.False(Bound().Matches("artifact-1", "v4", "hash-abc"));
    }

    [Fact]
    public void ABindingWithoutAnArtifactIsInvalid()
    {
        Assert.False(new PositionOwnership("", "v3", "hash", "family", DateTimeOffset.UnixEpoch).IsValid());
    }

    [Fact]
    public void TheDescriptionNamesWhatLicensedThePosition()
    {
        Assert.Contains("artifact-1@v3", Bound().Describe());
    }

    private static PositionOwnership Bound() =>
        new("artifact-1", "v3", "hash-abc", "momentum", DateTimeOffset.UnixEpoch);
}
