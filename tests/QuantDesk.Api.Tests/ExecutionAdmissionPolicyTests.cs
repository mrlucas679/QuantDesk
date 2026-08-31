using QuantDesk.Api.PaperTrading;
using QuantDesk.Domain.Execution;

namespace QuantDesk.Api.Tests;

public sealed class ExecutionAdmissionPolicyTests
{
    private readonly ExecutionAdmissionPolicy _policy = new();

    [Theory]
    [InlineData(OrderClassification.DiagnosticExecution, true, false, false, "ADMITTED")]
    [InlineData(OrderClassification.StrategyForwardResearch, false, true, false, "ADMITTED")]
    [InlineData(OrderClassification.QualifiedStrategy, false, false, true, "ADMITTED")]
    public void AdmitsOnlyTheReadinessDomainRequiredByTheOrderClass(
        OrderClassification classification,
        bool infrastructureReady,
        bool researchReady,
        bool fullyReady,
        string expectedReason)
    {
        FullSystemReadinessSnapshot readiness = Readiness(
            infrastructureReady, researchReady, fullyReady);

        bool admitted = _policy.IsAdmitted(classification, readiness, out string reason);

        Assert.True(admitted);
        Assert.Equal(expectedReason, reason);
    }

    [Theory]
    [InlineData(OrderClassification.DiagnosticExecution, "INFRASTRUCTURE_NOT_READY")]
    [InlineData(OrderClassification.StrategyForwardResearch, "STRATEGY_RESEARCH_NOT_READY")]
    [InlineData(OrderClassification.QualifiedStrategy, "QUALIFIED_STRATEGY_NOT_READY")]
    public void RejectsWhenTheRequiredReadinessDomainIsNotMet(
        OrderClassification classification, string expectedReason)
    {
        bool admitted = _policy.IsAdmitted(classification, Readiness(false, false, false), out string reason);

        Assert.False(admitted);
        Assert.Equal(expectedReason, reason);
    }

    private static FullSystemReadinessSnapshot Readiness(
        bool infrastructureReady, bool researchReady, bool ready) => new(
        MarketDataHealthy: ready,
        TradeUpdatesHealthy: ready,
        BrokerReconciled: infrastructureReady || researchReady || ready,
        PortfolioKnown: infrastructureReady || researchReady || ready,
        FeaturesReady: researchReady || ready,
        ExpertsReady: researchReady || ready,
        CommitteesReady: ready,
        RiskReady: infrastructureReady || researchReady || ready,
        ReservationReady: infrastructureReady || researchReady || ready,
        ExecutionReady: infrastructureReady || researchReady || ready,
        ExitEngineReady: ready,
        PaperEndpointVerified: infrastructureReady || researchReady || ready,
        UpdatedAt: DateTimeOffset.UtcNow);
}
