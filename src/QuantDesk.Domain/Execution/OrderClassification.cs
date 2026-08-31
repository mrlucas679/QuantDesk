namespace QuantDesk.Domain.Execution;

/// <summary>Separates execution-path diagnostics from strategy evidence.</summary>
public enum OrderClassification
{
    DiagnosticExecution,
    StrategyForwardResearch,
    QualifiedStrategy
}
