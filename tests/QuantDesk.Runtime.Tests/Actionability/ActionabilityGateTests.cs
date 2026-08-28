using QuantDesk.Domain.Numerics;
using QuantDesk.Domain.Strategies;
using QuantDesk.Runtime.Costs;
using QuantDesk.Runtime.Actionability;
using QuantDesk.Runtime.Tests.TestData;

namespace QuantDesk.Runtime.Tests.Actionability;

public sealed class ActionabilityGateTests
{
    [Fact]
    public void Evaluate_RejectsWideSpreadEvenWithPositiveForecast()
    {
        var gate = new ActionabilityGate(0.01, new Usd(1));
        var market = FinancialTestData.HealthyMarket() with { RelativeSpread = 0.02 };

        ActionabilityAssessment assessment = gate.Evaluate(
            FinancialTestData.Candidate(),
            new CostEstimate(Usd.Zero, Usd.Zero, Usd.Zero, Usd.Zero, Usd.Zero),
            market);

        Assert.False(assessment.Actionable);
        Assert.Equal(ActionabilityReason.SpreadTooWide, assessment.Reason);
    }
}
