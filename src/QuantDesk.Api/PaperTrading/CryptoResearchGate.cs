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
}

public sealed class CryptoResearchGate
{
    private const decimal RoundTripTakerFeeBps = 50m;
    private const decimal RoundTripSlippageAllowanceBps = 10m;
    private const decimal MinimumNetEdgeBps = 10m;

    public CryptoResearchDecision Evaluate(CryptoMarketEvidence evidence)
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
        decimal estimatedCost = spreadBps + RoundTripTakerFeeBps + RoundTripSlippageAllowanceBps;

        if (mediumMomentum <= 0 || shortMomentum <= 0)
            return new CryptoResearchDecision(false, expectedReturn, estimatedCost, "MOMENTUM_NOT_ALIGNED")
            {
                MediumMomentumBps = mediumMomentum,
                ShortMomentumBps = shortMomentum,
                SpreadBps = spreadBps
            };
        if (expectedReturn <= estimatedCost + MinimumNetEdgeBps)
            return new CryptoResearchDecision(false, expectedReturn, estimatedCost, "EXPECTED_EDGE_BELOW_COSTS")
            {
                MediumMomentumBps = mediumMomentum,
                ShortMomentumBps = shortMomentum,
                SpreadBps = spreadBps
            };
        return new CryptoResearchDecision(true, expectedReturn, estimatedCost, "RESEARCH_EDGE_APPROVED")
        {
            MediumMomentumBps = mediumMomentum,
            ShortMomentumBps = shortMomentum,
            SpreadBps = spreadBps
        };
    }

    private static decimal ReturnBps(decimal start, decimal end) =>
        start <= 0 ? decimal.MinValue : ((end / start) - 1m) * 10_000m;

    private static CryptoResearchDecision Reject(string reason) => new(false, 0, 0, reason);
}
