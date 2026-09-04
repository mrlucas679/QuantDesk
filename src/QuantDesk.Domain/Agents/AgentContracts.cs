using QuantDesk.Domain.Forecasts;
using QuantDesk.Domain.Numerics;

namespace QuantDesk.Domain.Agents;

public enum AgentRole
{
    Review,
    Research,
    Policy
}

public enum AgentEvaluationMode
{
    ForwardOnly,
    MaskedHistorical
}

public sealed record AgentToolCall(
    string ToolId,
    IReadOnlyDictionary<string, string> Arguments,
    bool MutatedExternalState);

public sealed record AgentInvocation(
    AgentRole Role,
    string SystemPrompt,
    string InputJson,
    string OutputContract,
    IReadOnlySet<string> AllowedTools);

public sealed record AgentCompletion(
    string ModelId,
    string OutputJson,
    IReadOnlyList<AgentToolCall> ToolCalls);

public sealed record EpisodeTraceStep(
    long Sequence,
    DateTimeOffset TimeUtc,
    string EventType,
    string EntityId,
    string PayloadHash)
{
    public bool IsValid() => Sequence > 0
        && !string.IsNullOrWhiteSpace(EventType)
        && !string.IsNullOrWhiteSpace(EntityId)
        && !string.IsNullOrWhiteSpace(PayloadHash);
}

public sealed record ForecastReviewEvidence(
    long ForecastId,
    int ExpertId,
    ForecastType ForecastType,
    double PredictedValue,
    double ObservedValue,
    string EvidenceId)
{
    public bool IsValid() => ForecastId > 0
        && ExpertId >= 0
        && Enum.IsDefined(ForecastType)
        && double.IsFinite(PredictedValue)
        && double.IsFinite(ObservedValue)
        && !string.IsNullOrWhiteSpace(EvidenceId);
}

public sealed record ReviewAgentInput(
    long EpisodeId,
    AgentEvaluationMode EvaluationMode,
    IReadOnlyList<EpisodeTraceStep> EpisodeTrace,
    IReadOnlyList<ForecastReviewEvidence> Forecasts,
    string StrategyEvidenceId,
    string CostEvidenceId,
    string RiskDecisionId,
    string ExecutionEvidenceId,
    string RealizedMarketPathHash)
{
    public bool IsValid() => EpisodeId > 0
        && Enum.IsDefined(EvaluationMode)
        && EpisodeTrace is { Count: > 0 }
        && EpisodeTrace.All(step => step.IsValid())
        && EpisodeTrace.Select(step => step.Sequence).SequenceEqual(
            Enumerable.Range(1, EpisodeTrace.Count).Select(value => (long)value))
        && Forecasts is not null
        && Forecasts.All(forecast => forecast.IsValid())
        && !string.IsNullOrWhiteSpace(StrategyEvidenceId)
        && !string.IsNullOrWhiteSpace(CostEvidenceId)
        && !string.IsNullOrWhiteSpace(RiskDecisionId)
        && !string.IsNullOrWhiteSpace(ExecutionEvidenceId)
        && !string.IsNullOrWhiteSpace(RealizedMarketPathHash);
}

public sealed record ForecastAssessment(
    long ForecastId,
    int ExpertId,
    ForecastType ForecastType,
    bool SupportedByOutcome,
    double Score,
    string Reason)
{
    public bool IsValid() => ForecastId > 0
        && ExpertId >= 0
        && Enum.IsDefined(ForecastType)
        && double.IsFinite(Score)
        && !string.IsNullOrWhiteSpace(Reason);
}

public sealed record ReviewAgentOutput(
    long EpisodeId,
    IReadOnlyList<ForecastAssessment> ForecastAssessment,
    string StrategyAssessment,
    string ExecutionAssessment,
    string RiskAssessment,
    IReadOnlyList<string> ResearchQuestions)
{
    public bool IsValid() => EpisodeId > 0
        && ForecastAssessment is not null
        && ForecastAssessment.All(item => item.IsValid())
        && !string.IsNullOrWhiteSpace(StrategyAssessment)
        && !string.IsNullOrWhiteSpace(ExecutionAssessment)
        && !string.IsNullOrWhiteSpace(RiskAssessment)
        && ResearchQuestions is not null
        && ResearchQuestions.All(question => !string.IsNullOrWhiteSpace(question));
}

public sealed record ResearchAgentInput(
    AgentEvaluationMode EvaluationMode,
    IReadOnlyList<string> ExperimentRegistryIds,
    IReadOnlyList<string> ResearchEvidenceIds,
    IReadOnlyList<string> ReviewFindingIds,
    IReadOnlyDictionary<string, double> DriftMetrics,
    IReadOnlyList<string> FailedHypothesisIds)
{
    public bool IsValid() => Enum.IsDefined(EvaluationMode)
        && ExperimentRegistryIds is not null
        && ExperimentRegistryIds.All(value => !string.IsNullOrWhiteSpace(value))
        && ResearchEvidenceIds is { Count: > 0 }
        && ResearchEvidenceIds.All(value => !string.IsNullOrWhiteSpace(value))
        && ReviewFindingIds is not null
        && ReviewFindingIds.All(value => !string.IsNullOrWhiteSpace(value))
        && DriftMetrics is not null
        && DriftMetrics.All(metric => !string.IsNullOrWhiteSpace(metric.Key) && double.IsFinite(metric.Value))
        && FailedHypothesisIds is not null
        && FailedHypothesisIds.All(value => !string.IsNullOrWhiteSpace(value));
}

public sealed record ResearchHypothesisProposal(
    string HypothesisId,
    string SemanticFamilyId,
    string EconomicMechanism,
    string CounterHypothesis,
    string FalsificationTest,
    IReadOnlyList<string> RequiredData,
    string Horizon,
    IReadOnlyList<string> CostAssumptions,
    string Baseline,
    string ValidationMethod,
    IReadOnlyList<string> SuccessMetrics,
    IReadOnlyList<string> FailureConditions,
    IReadOnlyList<string> EvidenceIds,
    bool PretrainingContaminationRisk)
{
    public bool IsValid() => !string.IsNullOrWhiteSpace(HypothesisId)
        && !string.IsNullOrWhiteSpace(SemanticFamilyId)
        && !string.IsNullOrWhiteSpace(EconomicMechanism)
        && !string.IsNullOrWhiteSpace(CounterHypothesis)
        && !string.IsNullOrWhiteSpace(FalsificationTest)
        && RequiredData is { Count: > 0 }
        && RequiredData.All(value => !string.IsNullOrWhiteSpace(value))
        && !string.IsNullOrWhiteSpace(Horizon)
        && CostAssumptions is { Count: > 0 }
        && CostAssumptions.All(value => !string.IsNullOrWhiteSpace(value))
        && !string.IsNullOrWhiteSpace(Baseline)
        && !string.IsNullOrWhiteSpace(ValidationMethod)
        && SuccessMetrics is { Count: > 0 }
        && SuccessMetrics.All(value => !string.IsNullOrWhiteSpace(value))
        && FailureConditions is { Count: > 0 }
        && FailureConditions.All(value => !string.IsNullOrWhiteSpace(value))
        && EvidenceIds is { Count: > 0 }
        && EvidenceIds.All(value => !string.IsNullOrWhiteSpace(value));
}

public sealed record PolicyAgentInput(
    IReadOnlySet<int> ValidatedExpertIds,
    string RegimeContextId,
    IReadOnlyList<string> ShadowEvidenceIds,
    string PortfolioRiskSummaryId,
    long CurrentPolicyVersion)
{
    public bool IsValid() => ValidatedExpertIds is { Count: > 0 }
        && ValidatedExpertIds.All(expert => expert >= 0)
        && !string.IsNullOrWhiteSpace(RegimeContextId)
        && ShadowEvidenceIds is { Count: > 0 }
        && ShadowEvidenceIds.All(value => !string.IsNullOrWhiteSpace(value))
        && !string.IsNullOrWhiteSpace(PortfolioRiskSummaryId)
        && CurrentPolicyVersion >= 0;
}

public sealed record PolicyAgentProposal(
    long PolicyVersion,
    IReadOnlySet<int> EnabledExperts,
    double MinimumConfidence,
    decimal MinimumNetEdgeUsd,
    double ExplorationFraction,
    double MaximumExpertWeight)
{
    public bool IsStructurallyValid() => PolicyVersion > 0
        && EnabledExperts is { Count: > 0 }
        && EnabledExperts.All(expert => expert >= 0)
        && double.IsFinite(MinimumConfidence)
        && MinimumConfidence is >= 0 and <= 1
        && MinimumNetEdgeUsd >= 0
        && double.IsFinite(ExplorationFraction)
        && double.IsFinite(MaximumExpertWeight);
}

public sealed record TradingPolicy(
    long Version,
    DateTimeOffset CreatedUtc,
    DateTimeOffset ExpiresUtc,
    IReadOnlySet<int> EnabledExperts,
    double MinimumConfidence,
    Usd MinimumNetEdge,
    double ExplorationFraction,
    double MaximumExpertWeight);

public sealed record PolicyBounds(
    double MinimumConfidenceFloor,
    Usd MinimumNetEdgeFloor,
    double MaximumExplorationFraction,
    double MaximumExpertWeightCeiling,
    IReadOnlySet<int> AllowedExperts)
{
    public bool IsValid() => double.IsFinite(MinimumConfidenceFloor)
        && MinimumConfidenceFloor is >= 0 and <= 1
        && MinimumNetEdgeFloor >= Usd.Zero
        && double.IsFinite(MaximumExplorationFraction)
        && MaximumExplorationFraction is >= 0 and <= 1
        && double.IsFinite(MaximumExpertWeightCeiling)
        && MaximumExpertWeightCeiling is > 0 and <= 1
        && AllowedExperts is { Count: > 0 }
        && AllowedExperts.All(expert => expert >= 0);
}

public enum AgentHypothesisTrialStatus
{
    Generated,
    Duplicate,
    RejectedBeforeImplementation,
    Implemented,
    Backtested,
    Promoted,
    Failed
}

public sealed record AgentHypothesisTrial(
    long TrialId,
    string HypothesisId,
    string SemanticFamilyId,
    DateTimeOffset GeneratedUtc,
    string AgentModelId,
    string PromptTemplateVersion,
    AgentHypothesisTrialStatus Status,
    string? ExperimentId);
