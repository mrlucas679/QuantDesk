using QuantDesk.Alpaca.MarketData;

namespace QuantDesk.Api.PaperTrading;

public sealed record CryptoResearchDecision(
    bool Approved,
    decimal ExpectedReturnBps,
    decimal EstimatedCostBps,
    string Reason)
{
    public decimal MediumMomentumBps { get; init; }
    public decimal ShortMomentumBps { get; init; }
    public decimal SpreadBps { get; init; }
    public int LookbackBars => 13;

    /// <summary>Asset class whose cost profile produced this decision.</summary>
    public string AssetClass { get; init; } = ExecutionCostProfile.SpotCryptoTaker.AssetClass;

    /// <summary>The total hurdle the expected move had to clear, for audit.</summary>
    public decimal HurdleBps { get; init; }
}

/// <summary>
/// Admits a two-horizon momentum opportunity only when the weaker of the two horizons clears the
/// venue's round-trip cost plus a margin. The cost profile is injected so the same logic serves
/// any asset class; it defaults to Alpaca spot crypto so existing callers are unchanged.
/// </summary>
public sealed class CryptoResearchGate(ExecutionCostProfile? costProfile = null)
{
    private readonly ExecutionCostProfile _costs = costProfile ?? ExecutionCostProfile.SpotCryptoTaker;

    public CryptoResearchDecision Evaluate(DirectionalMarketEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.Closes.Count < 13 || evidence.Bid <= 0 || evidence.Ask < evidence.Bid)
            return Reject("INSUFFICIENT_FRESH_EVIDENCE");

        decimal latest = evidence.Closes[^1];
        decimal mediumMomentum = ReturnBps(evidence.Closes[^13], latest);
        decimal shortMomentum = ReturnBps(evidence.Closes[^4], latest);
        decimal expectedReturn = Math.Min(mediumMomentum, shortMomentum);
        decimal mid = (evidence.Bid + evidence.Ask) / 2m;
        decimal spreadBps = mid <= 0 ? decimal.MaxValue : ((evidence.Ask - evidence.Bid) / mid) * 10_000m;
        decimal estimatedCost = spreadBps + _costs.RoundTripFeeBps + _costs.SlippageAllowanceBps;
        decimal hurdle = _costs.HurdleBps(spreadBps);

        if (mediumMomentum <= 0 || shortMomentum <= 0)
            return Decide(false, "MOMENTUM_NOT_ALIGNED", expectedReturn, estimatedCost, hurdle,
                mediumMomentum, shortMomentum, spreadBps);
        if (expectedReturn <= hurdle)
            return Decide(false, "EXPECTED_EDGE_BELOW_COSTS", expectedReturn, estimatedCost, hurdle,
                mediumMomentum, shortMomentum, spreadBps);
        return Decide(true, "RESEARCH_EDGE_APPROVED", expectedReturn, estimatedCost, hurdle,
            mediumMomentum, shortMomentum, spreadBps);
    }

    private CryptoResearchDecision Decide(
        bool approved, string reason, decimal expectedReturn, decimal estimatedCost, decimal hurdle,
        decimal mediumMomentum, decimal shortMomentum, decimal spreadBps) =>
        new(approved, expectedReturn, estimatedCost, reason)
        {
            MediumMomentumBps = mediumMomentum,
            ShortMomentumBps = shortMomentum,
            SpreadBps = spreadBps,
            AssetClass = _costs.AssetClass,
            HurdleBps = hurdle
        };

    private static decimal ReturnBps(decimal start, decimal end) =>
        start <= 0 ? decimal.MinValue : ((end / start) - 1m) * 10_000m;

    private static CryptoResearchDecision Reject(string reason) => new(false, 0, 0, reason);
}
