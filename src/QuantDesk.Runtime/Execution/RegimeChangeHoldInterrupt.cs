using QuantDesk.Domain.Forecasts;

namespace QuantDesk.Runtime.Execution;

/// <summary>Supplies the regime a symbol is currently in, or null when nothing can say.</summary>
public interface IRegimeSource
{
    MarketRegime? CurrentRegime(string symbol);
}

/// <summary>
/// Closes a position when the market has moved into stress since it was opened.
///
/// The rule that had no input
/// --------------------------
/// <c>PositionManagementPlan.ExitOnRegimeChange</c> has been true on every candidate since the
/// compiler was written and <c>ExitEngine</c> has implemented it throughout. It could not be wired
/// earlier today for one reason: the Regime forecast family was declared and never emitted, so the
/// rule would have been reading a number nothing computed. That is now fixed, so this is the rule
/// arriving rather than a new idea.
///
/// Stress specifically, not any change
/// -----------------------------------
/// A position opened in a trend that drifts into a range has not been invalidated -- it is simply
/// less likely to work, which the timer already handles. Exiting on every reclassification would
/// close positions constantly at the boundaries where the baseline is least certain, and each exit
/// costs a full round trip: 81.2 bps, measured. Stress is different in kind. It is the regime where
/// the spread widens, the book thins, and the distribution the position was sized against stops
/// applying, so the cost of staying rises faster than the cost of leaving.
///
/// Silence means hold
/// ------------------
/// No regime, an unreadable source, or anything short of stress leaves the position alone. A
/// context expert that cannot speak is not evidence that the market has turned, and closing on its
/// silence would convert every gap in the feed into a realised loss.
/// </summary>
public sealed class RegimeChangeHoldInterrupt(IRegimeSource regimes) : IHoldInterrupt
{
    public HoldInterrupt Evaluate(in HeldPosition position)
    {
        MarketRegime? regime;
        try
        {
            regime = regimes.CurrentRegime(position.Symbol);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return HoldInterrupt.None;
        }

        return regime is MarketRegime.Stress
            ? HoldInterrupt.Now("RegimeChanged:Stress")
            : HoldInterrupt.None;
    }
}
