using QuantDesk.Api.PaperTrading;
using QuantDesk.Domain.Execution;
using QuantDesk.Runtime.Time;

namespace QuantDesk.Api.Tests;

/// <summary>
/// The runtime mode answers "is the runtime healthy enough to act". Strategy qualification is a
/// different question, answered where strategy orders are admitted.
///
/// Conflating them meant a dark research plane held the runtime in EntryHalted forever, which silently
/// disabled the operator's manual controls. These tests pin that separating the two does not let an
/// unqualified strategy through.
/// </summary>
public sealed class RuntimeModeSeparationTests
{
    private static FullSystemReadinessSnapshot InfrastructureOnly()
    {
        var readiness = new FullSystemReadinessState(new LiveRuntimeClock());
        readiness.RecordDeterministicRuntime(true, true, true, true, true);
        readiness.RecordBrokerPreflight(reconciled: true, portfolioKnown: true, paperEndpointVerified: true);
        return readiness.Snapshot();   // research plane never recorded: features/experts stay false
    }

    [Fact]
    public void InfrastructureReadinessDoesNotImplyFullReadiness()
    {
        FullSystemReadinessSnapshot snapshot = InfrastructureOnly();

        Assert.True(snapshot.InfrastructureExecutionReady);
        Assert.False(snapshot.StrategyResearchReady);
        Assert.False(snapshot.Ready);
    }

    [Fact]
    public void AQualifiedStrategyIsStillRefusedWhileResearchIsDark()
    {
        // The property that must survive the separation. Infrastructure being fine is not permission
        // to run a strategy that has never qualified.
        var policy = new ExecutionAdmissionPolicy();

        bool admitted = policy.IsAdmitted(
            OrderClassification.QualifiedStrategy, InfrastructureOnly(),
            closingExposure: false, out string reason);

        Assert.False(admitted);
        Assert.Equal("QUALIFIED_STRATEGY_NOT_READY", reason);
    }

    [Fact]
    public void ForwardResearchOrdersAreAlsoStillRefused()
    {
        var policy = new ExecutionAdmissionPolicy();

        Assert.False(policy.IsAdmitted(
            OrderClassification.StrategyForwardResearch, InfrastructureOnly(),
            closingExposure: false, out _));
    }

    [Fact]
    public void TheDiagnosticAndManualPathsAreAdmittedOnInfrastructureAlone()
    {
        var policy = new ExecutionAdmissionPolicy();

        Assert.True(policy.IsAdmitted(
            OrderClassification.DiagnosticExecution, InfrastructureOnly(),
            closingExposure: false, out _));
    }
}
