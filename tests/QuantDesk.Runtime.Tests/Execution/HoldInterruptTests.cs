using QuantDesk.Domain.Execution;
using QuantDesk.Runtime.Execution;
using QuantDesk.Runtime.Persistence;

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

    private static SpotExecutionRecord Held(decimal entryPrice, decimal quantity, decimal maximumLoss) =>
        new("exec", "strategy", "BTC/USD", 0, SpotExecutionState.Holding, "entry", "exit", quantity,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)
        {
            DefinedMaximumLoss = maximumLoss,
            MaximumHoldingPeriod = TimeSpan.FromMinutes(5),
            EntryFilledQuantity = quantity,
            EntryAverageFillPrice = entryPrice,
        };

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

    private static SpotExecutionRecord Record() =>
        new("exec", "strategy", "BTC/USD", 0, SpotExecutionState.Holding, "entry", "exit", 1m,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    private sealed class Always(bool exit, string? reason) : IHoldInterrupt
    {
        public HoldInterrupt Evaluate(SpotExecutionRecord record) => new(exit, reason);
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
