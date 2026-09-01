using QuantDesk.Domain.Execution;

namespace QuantDesk.Api.PaperTrading;

/// <summary>
/// Classifies execution requests against the appropriate readiness domain, and names the refusal.
///
/// The rule itself lives on <see cref="FullSystemReadinessSnapshot.IsReadyFor"/>; this type adds only
/// the reason codes. It used to restate the rule, and the copy drifted: it required full readiness for
/// every order including a close, which is the deadlock that stranded a live position.
/// </summary>
public sealed class ExecutionAdmissionPolicy
{
    /// <param name="closingExposure">
    /// True when the order reduces an existing position. Admission is deliberately weaker for these:
    /// refusing to close is not a safe default, it is a stranded position.
    /// </param>
    public bool IsAdmitted(OrderClassification classification, FullSystemReadinessSnapshot readiness,
        bool closingExposure, out string reason)
    {
        ArgumentNullException.ThrowIfNull(readiness);
        bool admitted = readiness.IsReadyFor(classification, closingExposure);
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
