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

    private static HeldPosition Position(decimal entry, decimal quantity, decimal target) =>
        new("execution", "BTC/USD", quantity, entry, DefinedMaximumLoss: 50m,
            Ownership: null, EarliestLegExpiry: null, MinimumDaysToExpiry: null, ProfitTarget: target);

    private sealed class StubMarker(decimal? mid) : IHeldPositionMarker
    {
        public decimal? CurrentMid(string symbol) => mid;
    }
}
