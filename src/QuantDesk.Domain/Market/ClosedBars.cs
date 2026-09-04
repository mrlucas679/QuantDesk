namespace QuantDesk.Domain.Market;

/// <summary>
/// Which bars have actually finished, and are therefore safe to compute a decision from.
///
/// The defect this closes
/// ----------------------
/// The lane asked the venue for bars up to "now" and used every one it got back, including the bar
/// still forming. A decision taken at 13:57 was therefore computed from a 13:45-14:00 candle that
/// was twelve minutes old and three minutes from finished, and every indicator's most recent value
/// described a partial bar: a close that is not the close, a high and low that have not finished
/// moving, and a volume that is a fraction of what the bar will end with.
///
/// It is worse than a small inaccuracy, because the lane re-evaluates every few seconds. The same
/// bar produces a different answer on each pass as it fills in, so a breakout can fire, stop firing,
/// and fire again inside one candle -- and whichever pass happens to catch the extreme is the one
/// that opens a position. Volume rules are hit hardest: comparing a one-fifth-formed bar's volume
/// against completed bars makes every bar look quiet early and expand through its own life, which
/// is a signal generated entirely by the clock.
///
/// The rule
/// --------
/// A bar stamped at <c>t</c> covers <c>[t, t + duration)</c> and is finished once the clock has
/// passed its end. Alpaca stamps bars with their opening time, so this is a strict comparison
/// against the end rather than the stamp.
/// </summary>
public static class ClosedBars
{
    /// <summary>
    /// How many of the trailing bars are complete, given when the newest one opened.
    ///
    /// Returns a count rather than a filtered list because callers hold several parallel series --
    /// closes, highs, lows, volumes -- that are read by index and must be truncated together. A
    /// helper that returned one filtered series would leave the others ragged, and windowed
    /// indicators read across them.
    /// </summary>
    /// <param name="timestamps">Bar opening times, ascending.</param>
    /// <param name="barDuration">The period each bar covers.</param>
    /// <param name="now">Current time.</param>
    public static int CompletedCount(
        IReadOnlyList<DateTimeOffset> timestamps, TimeSpan barDuration, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(timestamps);
        if (barDuration <= TimeSpan.Zero) return timestamps.Count;

        int count = timestamps.Count;
        while (count > 0 && timestamps[count - 1] + barDuration > now) count--;

        return count;
    }

    /// <summary>
    /// Whether the newest bar in a series has closed.
    ///
    /// Useful on its own for a lane that should only *act* on a bar boundary rather than merely
    /// avoid reading a partial bar: a 15-minute strategy that recomputes every five seconds is
    /// taking fifteen-minute decisions on a five-second clock, and will take whichever of its 180
    /// looks at the same bar happened to be most extreme.
    /// </summary>
    public static bool NewestIsClosed(
        IReadOnlyList<DateTimeOffset> timestamps, TimeSpan barDuration, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(timestamps);

        return timestamps.Count == 0
            || CompletedCount(timestamps, barDuration, now) == timestamps.Count;
    }
}
