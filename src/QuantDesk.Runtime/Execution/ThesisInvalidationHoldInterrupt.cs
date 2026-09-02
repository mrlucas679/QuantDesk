namespace QuantDesk.Runtime.Execution;

/// <summary>
/// Closes a position whose strategy stopped being one the system is willing to trade.
///
/// The gap this fills
/// ------------------
/// <c>PositionManagementPlan.ExitOnThesisInvalidation</c> has been set to true on every candidate
/// the compiler produces, and <c>ExitEngine</c> has implemented the rule since the beginning. The
/// engine is registered in the container and reported in readiness, and no live spot position has
/// ever consulted it: the durable lifecycles exit on their timer and on the interrupts wired here.
/// So the plan said "exit when the thesis fails", the code to do it existed, and a position whose
/// thesis had failed ran to its four-hour timer anyway.
///
/// What counts as the thesis failing
/// ---------------------------------
/// Narrowly: the rule that opened the position is no longer in the tradable book. That happens when
/// live evidence demotes it, when re-measurement moves it past the known-loser test, or when its
/// research is marked stale. All three are the system deciding it would not open this position
/// again -- which is exactly the thing a position already open should not be waiting out a timer
/// on. It happened on 2026-09-02: every rule in both books became a known loser at 16:22Z while a
/// position opened at 11:36Z under one of them was still held.
///
/// What is deliberately not here
/// -----------------------------
/// Not "the entry signal stopped firing". The research measured fixed-horizon holds, so a rule that
/// exits early on a condition the backtest never modelled is a different rule than the one that was
/// measured, and its figures would no longer describe it. Adding that would recreate the staleness
/// problem it took a re-measurement to find.
///
/// Not regime change either, though the plan has a flag for it. No regime forecast is produced
/// anywhere in the live path -- the family is declared and never emitted -- so a regime rule here
/// would be reading a number nothing computes. It is honest to leave it unimplemented and say so.
/// </summary>
/// <param name="tradableStrategies">The rules currently tradable for a given symbol.</param>
public sealed class ThesisInvalidationHoldInterrupt(
    Func<string, IReadOnlyList<string>> tradableStrategies) : IHoldInterrupt
{
    public HoldInterrupt Evaluate(in HeldPosition position)
    {
        if (string.IsNullOrWhiteSpace(position.StrategyId)) return HoldInterrupt.None;

        IReadOnlyList<string> tradable;
        try
        {
            tradable = tradableStrategies(position.Symbol);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A book that cannot be read is not a book that has changed its mind. Closing on a
            // lookup failure would turn a transient fault into a realised loss.
            return HoldInterrupt.None;
        }

        // An empty book is ambiguous in the wrong direction: it is what an unroutable symbol
        // returns and also what a fully stood-down asset class returns. Treating it as
        // invalidation would flatten every position the moment routing hiccupped, so only a
        // populated book that excludes this rule counts.
        if (tradable.Count == 0) return HoldInterrupt.None;
        if (tradable.Contains(position.StrategyId, StringComparer.Ordinal)) return HoldInterrupt.None;

        return HoldInterrupt.Now($"ThesisInvalidated:{position.StrategyId}");
    }
}
