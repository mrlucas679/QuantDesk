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

        bool admitted = _policy.IsAdmitted(classification, readiness, closingExposure: false, out string reason);

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
        bool admitted = _policy.IsAdmitted(classification, Readiness(false, false, false), closingExposure: false, out string reason);

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

    [Theory]
    [InlineData(OrderClassification.DiagnosticExecution)]
    [InlineData(OrderClassification.StrategyForwardResearch)]
    [InlineData(OrderClassification.QualifiedStrategy)]
    public void EveryClassificationMayCloseWhileTheAccountIsNotFlat(OrderClassification classification)
    {
        // brokerReconciled means "the account is flat", so it is false precisely while a position that
        // needs closing exists. Requiring it to close was a deadlock; requiring research or strategy
        // readiness to close would be a stranger one, since neither is a reason to keep a position.
        var readiness = new FullSystemReadinessState();
        readiness.RecordDeterministicRuntime(true, true, true, true, true);
        readiness.RecordBrokerPreflight(reconciled: false, portfolioKnown: true, paperEndpointVerified: true);

        bool admitted = _policy.IsAdmitted(
            classification, readiness.Snapshot(), closingExposure: true, out string reason);

        Assert.True(admitted, reason);
    }

    [Theory]
    [InlineData(OrderClassification.DiagnosticExecution)]
    [InlineData(OrderClassification.QualifiedStrategy)]
    public void LosingBrokerTruthStillRefusesAClose(OrderClassification classification)
    {
        var readiness = new FullSystemReadinessState();
        readiness.RecordDeterministicRuntime(true, true, true, true, true);
        readiness.RecordBrokerPreflight(reconciled: false, portfolioKnown: false, paperEndpointVerified: false);

        Assert.False(_policy.IsAdmitted(
            classification, readiness.Snapshot(), closingExposure: true, out _));
    }
}
