using QuantDesk.Alpaca.MarketData;
using QuantDesk.Api.PaperTrading;
using QuantDesk.Domain.Numerics;
using QuantDesk.Domain.Portfolio;
using QuantDesk.Domain.Risk;
using QuantDesk.Runtime.Actionability;
using QuantDesk.Runtime.Costs;
using QuantDesk.Runtime.Experts;
using QuantDesk.Runtime.Risk;
using QuantDesk.Runtime.State;
using QuantDesk.Runtime.Strategies;
using QuantDesk.Runtime.Time;

namespace QuantDesk.Api.Tests;

public sealed class AutonomousDecisionPipelineTests
{
    [Fact]
    public void AlignedExpertsWithNetEdgeProduceManagedRiskApprovedCandidate()
    {
        AutonomousPipelineDecision result = CreatePipeline().Evaluate(
            0, Evidence(100m, 100.01m, 100m, 104m), Portfolio(), true, true);

        Assert.True(result.Approved);
        Assert.Equal("crypto-long-momentum-v1", result.Candidate?.StrategyId);
        Assert.Equal("crypto-long-managed-v1", result.Candidate?.ManagementPlan.ExitPolicyVersion);
        Assert.True(result.Risk?.Approved);
        Assert.Equal(2, result.Committee?.SupportingExperts.Count);
    }

    [Fact]
    public void PositiveForecastThatCannotPayCostsIsRejected()
    {
        AutonomousPipelineDecision result = CreatePipeline().Evaluate(
            0, Evidence(100m, 100.40m, 100m, 100.5m), Portfolio(), true, true);

        Assert.False(result.Approved);
        Assert.Equal("EXPECTED_EDGE_BELOW_COSTS", result.Reason);
        Assert.Null(result.Risk);
    }

    [Fact]
    public void ResearchGateRejectionCannotReachActionabilityOrRisk()
    {
        AutonomousPipelineDecision result = CreatePipeline().Evaluate(
            0, Evidence(100m, 100.01m, 100m, 100.3m), Portfolio(), true, true);

        Assert.False(result.Approved);
        Assert.Equal("EXPECTED_EDGE_BELOW_COSTS", result.Reason);
        Assert.Null(result.Candidate);
        Assert.Null(result.Risk);
    }

    [Fact]
    public void BrokerHealthFailureCannotReachApproval()
    {
        AutonomousPipelineDecision result = CreatePipeline().Evaluate(
            0, Evidence(100m, 100.01m, 100m, 104m), Portfolio(), false, true);

        Assert.False(result.Approved);
        Assert.Equal(RiskReason.BrokerUnhealthy.ToString(), result.Reason);
    }

    private static AutonomousDecisionPipeline CreatePipeline()
    {
        var clock = new VirtualRuntimeClock(DateTimeOffset.Parse("2026-08-29T00:00:00Z"));
        return new AutonomousDecisionPipeline(
            new MarketStateStore(1),
            new ExpertCommittee(0.6, 1),
            new CryptoDirectionalStrategyCompiler(new Usd(20), 0.05,
                TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)),
            new CryptoResearchGate(),
            new CryptoCostModel(new BasisPoints(50), new BasisPoints(10)),
            new ActionabilityGate(0.01, new Usd(0.01m)),
            new RiskGovernor(new RiskLimits(new Usd(5), new Usd(25), new Usd(100),
                new Usd(250), 1, 100_000, 100_000, 100_000, 0.01, 1)),
            clock);
    }

    private static CryptoMarketEvidence Evidence(
        decimal bid, decimal ask, decimal first, decimal last)
    {
        decimal step = (last - first) / 12m;
        decimal[] closes = Enumerable.Range(0, 13).Select(index => first + step * index).ToArray();
        return new CryptoMarketEvidence(bid, ask, closes);
    }

    private static PortfolioSnapshot Portfolio() => new(
        0, new Usd(100_000), new Usd(100_000), new Usd(100_000),
        Usd.Zero, Usd.Zero, Usd.Zero, Usd.Zero, 0, 0, 0, 0, 0, 0, 0, []);
}
