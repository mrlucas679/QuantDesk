namespace QuantDesk.Alpaca.MarketData;

/// <summary>
/// Bid, ask, and causal bar history consumed by directional strategy evaluation for any supported
/// asset class. Instrument routing selects the data source; this contract is intentionally
/// asset-neutral.
///
/// Why it carries more than closes
/// -------------------------------
/// It used to carry closes alone, which silently limited every strategy to what a sequence of
/// closing prices can express: momentum, moving averages, and little else. Whole families of
/// evidence were unreachable -- true range and every volatility measure built on it need the high
/// and the low, Stochastic and Donchian need the extremes of the window, and volume-weighted or
/// flow-based measures need volume. Adding them is what lets a strategy see the bar rather than
/// only its last price.
///
/// The extra series are optional so a source that genuinely has no volume is described honestly
/// rather than padded with zeroes, and a strategy that needs one it does not have declines instead
/// of computing something meaningless.
/// </summary>
public sealed record DirectionalMarketEvidence(decimal Bid, decimal Ask, IReadOnlyList<decimal> Closes)
{
    /// <summary>Bar highs, aligned with <see cref="Closes"/>, or empty when unavailable.</summary>
    public IReadOnlyList<decimal> Highs { get; init; } = [];

    /// <summary>Bar lows, aligned with <see cref="Closes"/>, or empty when unavailable.</summary>
    public IReadOnlyList<decimal> Lows { get; init; } = [];

    /// <summary>Bar volumes, aligned with <see cref="Closes"/>, or empty when unavailable.</summary>
    public IReadOnlyList<decimal> Volumes { get; init; } = [];

    /// <summary>
    /// The opening instant of each bar, aligned with <see cref="Closes"/>, or empty when unavailable.
    ///
    /// Why a bar series needs a time axis
    /// ----------------------------------
    /// Without one, every horizon in the system is counted in bars: "twelve bars ago" stands in for
    /// "an hour ago" and the two coincide only while the feed returns an unbroken sequence. On a
    /// venue that halts, a feed that drops a bar, or an equity series that crosses a session
    /// boundary, they diverge silently -- the rule still computes a number, and the number now
    /// describes a different span than the one it was calibrated on. The engineering constitution
    /// states the rule directly: horizons are time-based, never "N samples means N minutes".
    ///
    /// The time axis is also what makes a session knowable. A rolling window cannot tell where one
    /// trading day ends and the next begins, so a session-scoped measure like VWAP either resets on
    /// nothing or resets on a bar count that drifts away from the session it is meant to track.
    ///
    /// Both clients already parsed the bar's "t" field and discarded it.
    /// </summary>
    public IReadOnlyList<DateTimeOffset> Timestamps { get; init; } = [];

    /// <summary>True when highs, lows, and volumes are all present and aligned with the closes.</summary>
    public bool HasFullBars =>
        Highs.Count == Closes.Count && Lows.Count == Closes.Count && Volumes.Count == Closes.Count
        && Closes.Count > 0;

    /// <summary>True when every bar carries the instant it opened.</summary>
    public bool HasTimestamps => Timestamps.Count == Closes.Count && Closes.Count > 0;
}
