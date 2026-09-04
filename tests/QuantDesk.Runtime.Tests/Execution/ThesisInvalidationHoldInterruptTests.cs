using QuantDesk.Runtime.Execution;

namespace QuantDesk.Runtime.Tests.Execution;

/// <summary>
/// Closing a position whose strategy the system would no longer open it with.
///
/// PositionManagementPlan.ExitOnThesisInvalidation has been true on every candidate since the
/// compiler was written, and ExitEngine has implemented the rule for as long. The engine is
/// registered in the container and reported in readiness, and no live position ever called it --
/// so on 2026-09-02 every rule in both books became a known loser at 16:22Z while a position opened
/// at 11:36Z under one of them kept running to its four-hour timer.
/// </summary>
public sealed class ThesisInvalidationHoldInterruptTests
{
    [Fact]
    public void APositionWhoseRuleWasStoodDownIsClosed()
    {
        HoldInterrupt interrupt =
            new ThesisInvalidationHoldInterrupt(_ => ["some.other.rule.v1"])
                .Evaluate(Position("breakout.bollinger-upper.v1"));

        Assert.True(interrupt.ShouldExitNow);
        Assert.Equal("ThesisInvalidated:breakout.bollinger-upper.v1", interrupt.Reason);
    }

    [Fact]
    public void APositionWhoseRuleIsStillTradableIsLeftAlone()
    {
        Assert.False(
            new ThesisInvalidationHoldInterrupt(_ => ["a.rule.v1", "breakout.bollinger-upper.v1"])
                .Evaluate(Position("breakout.bollinger-upper.v1"))
                .ShouldExitNow);
    }

    [Fact]
    public void AnEmptyBookIsNotTreatedAsInvalidation()
    {
        // Ambiguous in the wrong direction: an empty list is what an unroutable symbol returns and
        // also what a fully stood-down asset class returns. Reading it as invalidation would
        // flatten every open position the moment routing hiccupped.
        Assert.False(
            new ThesisInvalidationHoldInterrupt(_ => [])
                .Evaluate(Position("breakout.bollinger-upper.v1"))
                .ShouldExitNow);
    }

    [Fact]
    public void ABookThatCannotBeReadIsNotABookThatChangedItsMind()
    {
        // Closing on a lookup failure would turn a transient fault into a realised loss.
        Assert.False(
            new ThesisInvalidationHoldInterrupt(_ => throw new InvalidOperationException("router down"))
                .Evaluate(Position("breakout.bollinger-upper.v1"))
                .ShouldExitNow);
    }

    [Fact]
    public void APositionWithNoRecordedRuleIsLeftAlone()
    {
        // Older records predate the strategy being carried on the view. Absence of a rule is not
        // evidence that the rule was withdrawn.
        Assert.False(
            new ThesisInvalidationHoldInterrupt(_ => ["a.rule.v1"])
                .Evaluate(Position(null))
                .ShouldExitNow);
    }

    private static HeldPosition Position(string? strategyId) => new(
        ExecutionId: "SPOT-1",
        Symbol: "AVAX/USD",
        Quantity: 28m,
        EntryPrice: 7.125m,
        DefinedMaximumLoss: 10m,
        Ownership: null,
        EarliestLegExpiry: null,
        StrategyId: strategyId);
}
