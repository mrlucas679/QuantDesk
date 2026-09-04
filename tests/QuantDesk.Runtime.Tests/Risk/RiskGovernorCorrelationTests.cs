using QuantDesk.Domain.Numerics;
using QuantDesk.Domain.Strategies;
using QuantDesk.Domain.Risk;
using QuantDesk.Runtime.Risk;
using QuantDesk.Runtime.Tests.TestData;

namespace QuantDesk.Runtime.Tests.Risk;

/// <summary>
/// The correlation limit, enforced where risk is actually decided.
///
/// CommonExposureLimit and DuplicateExposure have been in RiskReason since the beginning and
/// nothing ever raised either. The cap lived at the autonomous lane's entry gate, which left every
/// other path to a position -- the diagnostic lane, the options lane, anything added later -- free
/// of it. Risk is supposed to be deterministic, independent and final; a control one caller applies
/// to itself is none of those.
/// </summary>
public sealed class RiskGovernorCorrelationTests
{
    [Fact]
    public void ABookExposedBeyondTheCapIsRejectedByTheGovernorItself()
    {
        RiskDecision decision = Evaluate(
            limit: 600m, projectedCorrelatedExposure: 1_213m);

        Assert.False(decision.Approved);
        Assert.Equal(RiskReason.CommonExposureLimit, decision.Reason);
    }

    [Fact]
    public void ABookWithinTheCapIsApproved()
    {
        Assert.True(Evaluate(limit: 600m, projectedCorrelatedExposure: 529m).Approved);
    }

    [Fact]
    public void ExactlyAtTheCapIsAllowed()
    {
        Assert.True(Evaluate(limit: 600m, projectedCorrelatedExposure: 600m).Approved);
    }

    [Fact]
    public void ACallerThatSuppliesNoMeasurementLeavesTheCheckInert()
    {
        // Deliberate, and the reason the lane's own test pins that it passes the figure. A governor
        // that rejected on an absent measurement would refuse every candidate from any caller not
        // yet updated, which is a worse failure than the one being guarded against -- and the
        // measurement needs return history the governor is not allowed to go and fetch.
        Assert.True(Evaluate(limit: 1m, projectedCorrelatedExposure: 0m).Approved);
    }

    private static RiskDecision Evaluate(decimal limit, decimal projectedCorrelatedExposure)
    {
        RiskLimits limits = FinancialTestData.Limits() with
        {
            MaximumCorrelatedExposure = new Usd(limit),
        };

        return new RiskGovernor(limits).Evaluate(
            FinancialTestData.Candidate(),
            new CostEstimate(Usd.Zero, Usd.Zero, Usd.Zero, Usd.Zero, Usd.Zero),
            FinancialTestData.HealthyMarket(),
            FinancialTestData.Portfolio(),
            brokerHealthy: true,
            portfolioReconciled: true,
            nowTicks: 0,
            new Usd(projectedCorrelatedExposure));
    }
}
