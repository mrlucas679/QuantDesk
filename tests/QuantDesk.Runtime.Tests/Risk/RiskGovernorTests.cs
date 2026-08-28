using QuantDesk.Domain.Numerics;
using QuantDesk.Domain.Risk;
using QuantDesk.Domain.Strategies;
using QuantDesk.Runtime.Risk;
using QuantDesk.Runtime.Tests.TestData;

namespace QuantDesk.Runtime.Tests.Risk;

public sealed class RiskGovernorTests
{
    [Fact]
    public void Evaluate_RejectsGoldenOpenRiskCase()
    {
        var governor = new RiskGovernor(FinancialTestData.Limits());
        var portfolio = FinancialTestData.Portfolio(openRisk: 1_100, reservedRisk: 400);
        TradeCandidate candidate = FinancialTestData.Candidate(stressLoss: 700);
        var costs = new CostEstimate(Usd.Zero, Usd.Zero, Usd.Zero, Usd.Zero, Usd.Zero);
        var market = FinancialTestData.HealthyMarket();

        RiskDecision result = governor.Evaluate(
            candidate, costs, market, portfolio, true, true, nowTicks: 50);

        Assert.False(result.Approved);
        Assert.Equal(RiskReason.OpenRiskLimit, result.Reason);
    }

    [Fact]
    public void Evaluate_RejectsUnreconciledPortfolioBeforeEconomics()
    {
        var governor = new RiskGovernor(FinancialTestData.Limits());
        TradeCandidate candidate = FinancialTestData.Candidate();
        var costs = new CostEstimate(Usd.Zero, Usd.Zero, Usd.Zero, Usd.Zero, Usd.Zero);
        var market = FinancialTestData.HealthyMarket();

        RiskDecision result = governor.Evaluate(
            candidate, costs, market, FinancialTestData.Portfolio(), true, false, nowTicks: 50);

        Assert.Equal(RiskReason.PortfolioUnreconciled, result.Reason);
    }
}

