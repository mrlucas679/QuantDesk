using QuantDesk.Runtime.Execution;

namespace QuantDesk.Runtime.Tests.Execution;

public sealed class ProfitTargetHoldInterruptTests
{
    [Fact]
    public void APositionThatHasEarnedItsThesisIsClosed()
    {
        // The defect this closes. The exit engine had a maximum loss and a timer and nothing
        // between them, so being right bought nothing: the gain was held until the clock ran out.
        // On 2026-09-02 UNI/USD moved 9.43% while the lane held it and the lane captured 0.17%.
        var interrupt = new ProfitTargetHoldInterrupt(new StubMarker(110m));

        HoldInterrupt result = interrupt.Evaluate(Position(entry: 100m, quantity: 1m, target: 8m));

        Assert.True(result.ShouldExitNow);
        Assert.StartsWith("ProfitTargetReached:", result.Reason);
    }

    [Fact]
    public void APositionShortOfItsTargetKeepsRunning()
    {
        var interrupt = new ProfitTargetHoldInterrupt(new StubMarker(107m));

        Assert.False(interrupt.Evaluate(Position(entry: 100m, quantity: 1m, target: 8m)).ShouldExitNow);
    }

    [Fact]
    public void ExactlyReachingTheTargetCounts()
    {
        var interrupt = new ProfitTargetHoldInterrupt(new StubMarker(108m));

        Assert.True(interrupt.Evaluate(Position(entry: 100m, quantity: 1m, target: 8m)).ShouldExitNow);
    }

    [Fact]
    public void NoTargetMeansTheRuleStandsDownEntirely()
    {
        // Records written before the target existed load with zero, and must keep behaving exactly
        // as they did: bounded by the timer and the adverse-loss stop, and by nothing else.
        var interrupt = new ProfitTargetHoldInterrupt(new StubMarker(1_000m));

        Assert.False(interrupt.Evaluate(Position(entry: 100m, quantity: 1m, target: 0m)).ShouldExitNow);
    }

    [Fact]
    public void AMissingQuoteDoesNotCloseAWinningPosition()
    {
        // The same refusal the adverse-loss stop makes, for the same reason: acting on absent data
        // is acting on nothing. The scheduled exit still bounds the hold.
        var interrupt = new ProfitTargetHoldInterrupt(new StubMarker(null));

        Assert.False(interrupt.Evaluate(Position(entry: 100m, quantity: 1m, target: 1m)).ShouldExitNow);
    }

    [Fact]
    public void APositionWithNoEntryPriceCannotBeMarked()
    {
        var interrupt = new ProfitTargetHoldInterrupt(new StubMarker(500m));

        Assert.False(interrupt.Evaluate(
            new HeldPosition("e", "BTC/USD", 1m, null, 10m, null, null, null, 1m)).ShouldExitNow);
    }

    [Fact]
    public void TheTargetScalesWithQuantityBecauseItIsMoneyNotPrice()
    {
        // Two units earn the target on half the price move one unit needs. Comparing a price
        // distance against a money target would make the rule mean different things at different
        // sizes, which is the mistake the adverse-loss rule already avoids.
        var interrupt = new ProfitTargetHoldInterrupt(new StubMarker(104m));

        Assert.True(interrupt.Evaluate(Position(entry: 100m, quantity: 2m, target: 8m)).ShouldExitNow);
        Assert.False(interrupt.Evaluate(Position(entry: 100m, quantity: 1m, target: 8m)).ShouldExitNow);
    }

    [Fact]
    public void TheTargetIsNetOfTheExitStillToBePaid()
    {
        // A position sitting exactly on its target has not earned it. Alpaca charges the spot
        // crypto fee in kind on the way out as well as the way in, so closing costs another 25 bps
        // -- taking the target on a gross mark banks a quarter of a percent less than the target
        // claims, every time.
        //
        // 100 units bought at 100, marked at 108. Gross that is +800 and clears an 800 target; net
        // of a 25 bps exit on 10,800 of proceeds it is +773, and does not.
        var interrupt = new ProfitTargetHoldInterrupt(new StubMarker(108m));

        HeldPosition position = Position(entry: 100m, quantity: 100m, target: 800m)
            with { ExitCostRate = 0.0025m };

        Assert.False(interrupt.Evaluate(position).ShouldExitNow);
        Assert.True(interrupt.Evaluate(position with { ExitCostRate = 0m }).ShouldExitNow);
    }

    [Fact]
    public void TheMarkUsesWhatTheAccountHoldsNotWhatTheEntryBought()
    {
        // The venue took its entry fee in kind, so an entry that filled 100 leaves 99.75 to sell.
        // Marking the filled quantity claims a quarter of a percent of gain the account does not
        // have, and would take the target early on every position.
        var interrupt = new ProfitTargetHoldInterrupt(new StubMarker(110m));

        HeldPosition asBought = Position(entry: 100m, quantity: 100m, target: 990m);
        HeldPosition asHeld = asBought with { SellableQuantity = 99.75m };

        Assert.True(interrupt.Evaluate(asBought).ShouldExitNow);      // 1000 gross clears 990
        Assert.False(interrupt.Evaluate(asHeld).ShouldExitNow);       // 972.5 does not
    }

    private static HeldPosition Position(decimal entry, decimal quantity, decimal target) =>
        new("execution", "BTC/USD", quantity, entry, DefinedMaximumLoss: 50m,
            Ownership: null, EarliestLegExpiry: null, MinimumDaysToExpiry: null, ProfitTarget: target);

    private sealed class StubMarker(decimal? mid) : IHeldPositionMarker
    {
        public decimal? CurrentMid(string symbol) => mid;
    }
}
