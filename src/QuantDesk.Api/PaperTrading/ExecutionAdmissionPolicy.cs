using QuantDesk.Domain.Execution;

namespace QuantDesk.Api.PaperTrading;

/// <summary>Classifies execution requests against the appropriate readiness domain.</summary>
public sealed class ExecutionAdmissionPolicy
{
    public bool IsAdmitted(OrderClassification classification, FullSystemReadinessSnapshot readiness,
        out string reason)
    {
        var admitted = classification switch
        {
            OrderClassification.DiagnosticExecution => readiness.InfrastructureExecutionReady,
            OrderClassification.StrategyForwardResearch => readiness.StrategyResearchReady,
            OrderClassification.QualifiedStrategy => readiness.Ready,
            _ => false
        };
        reason = admitted ? "ADMITTED" : classification switch
        {
            OrderClassification.DiagnosticExecution => "INFRASTRUCTURE_NOT_READY",
            OrderClassification.StrategyForwardResearch => "STRATEGY_RESEARCH_NOT_READY",
            OrderClassification.QualifiedStrategy => "QUALIFIED_STRATEGY_NOT_READY",
            _ => "UNKNOWN_ORDER_CLASSIFICATION"
        };
        return admitted;
    }
}
