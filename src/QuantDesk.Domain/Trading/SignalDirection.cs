namespace QuantDesk.Domain.Trading;

/// <summary>
/// Which way a rule says to take exposure, if any.
///
/// Why this type exists
/// --------------------
/// A strategy used to answer <c>bool</c>: it fired, or it did not. Direction was therefore not
/// something a rule could express, and the execution path assumed the only answer -- every entry
/// was a buy, and the sole sell was the close of a long. Thirteen rules, all of them the bullish
/// half of a symmetric idea: RSI crossing up out of oversold with no overbought counterpart, a
/// close above the Donchian high with no test of the low, a VWAP gap read only when price sat
/// below.
///
/// So the system could not be wrong about direction, in the way a stopped clock cannot be wrong
/// about the time: it had no way to say anything else. In a falling market the best available
/// outcome was to abstain, and every measured edge in both books was negative.
///
/// <see cref="None"/> is a real answer and not an absence. A rule that has looked and found nothing
/// is different from one whose inputs were unavailable, and section 26.2 treats a refusal to commit
/// as information rather than as a weak signal to be averaged into a position.
/// </summary>
public enum SignalDirection
{
    /// <summary>The rule sees nothing worth taking exposure to.</summary>
    None,

    /// <summary>Take long exposure.</summary>
    Long,

    /// <summary>Take short exposure.</summary>
    Short
}
