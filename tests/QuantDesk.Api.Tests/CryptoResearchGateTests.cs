using QuantDesk.Alpaca.MarketData;
using QuantDesk.Api.PaperTrading;

namespace QuantDesk.Api.Tests;

public sealed class CryptoResearchGateTests
{
    [Fact]
    public void RejectsMomentumThatCannotCoverRoundTripCosts()
    {
        decimal[] closes = Enumerable.Range(0, 13)
            .Select(index => 100m + (index * 0.03m))
            .ToArray();
        var evidence = new DirectionalMarketEvidence(100.34m, 100.36m, closes);

        CryptoResearchDecision decision = new CryptoResearchGate().Evaluate(evidence);

        Assert.False(decision.Approved);
        Assert.Equal("EXPECTED_EDGE_BELOW_COSTS", decision.Reason);
    }

    [Fact]
    public void ApprovesAlignedMomentumWithConservativeNetEdge()
    {
        decimal[] closes =
        [
            100m, 100.1m, 100.2m, 100.3m, 100.4m, 100.5m, 100.6m,
            100.7m, 100.8m, 101m, 101.4m, 101.8m, 102.2m
        ];
        var evidence = new DirectionalMarketEvidence(102.19m, 102.21m, closes);

        CryptoResearchDecision decision = new CryptoResearchGate().Evaluate(evidence);

        Assert.True(decision.Approved);
        Assert.True(decision.ExpectedReturnBps > decision.EstimatedCostBps + 10m);
    }

    [Fact]
    public void RejectsConflictingShortTermMomentum()
    {
        decimal[] closes =
        [
            100m, 100.2m, 100.4m, 100.6m, 100.8m, 101m, 101.2m,
            101.4m, 101.6m, 102m, 101.8m, 101.6m, 101.4m
        ];
        var evidence = new DirectionalMarketEvidence(101.39m, 101.41m, closes);

        CryptoResearchDecision decision = new CryptoResearchGate().Evaluate(evidence);

        Assert.False(decision.Approved);
        Assert.Equal("MOMENTUM_NOT_ALIGNED", decision.Reason);
    }
}
