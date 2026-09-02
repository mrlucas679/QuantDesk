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

    /// <summary>True when highs, lows, and volumes are all present and aligned with the closes.</summary>
    public bool HasFullBars =>
        Highs.Count == Closes.Count && Lows.Count == Closes.Count && Volumes.Count == Closes.Count
        && Closes.Count > 0;
}
