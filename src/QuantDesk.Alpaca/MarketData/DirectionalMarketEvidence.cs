namespace QuantDesk.Alpaca.MarketData;

/// <summary>
/// Bid, ask, and causal closes consumed by directional strategy evaluation for any supported
/// asset class. Instrument routing selects the data source; this contract is intentionally
/// asset-neutral.
/// </summary>
public sealed record DirectionalMarketEvidence(decimal Bid, decimal Ask, IReadOnlyList<decimal> Closes);
