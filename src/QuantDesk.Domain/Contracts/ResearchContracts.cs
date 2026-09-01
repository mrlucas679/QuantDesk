namespace QuantDesk.Domain.Contracts;

public sealed record FeatureSchemaContract(
    string SchemaVersion,
    IReadOnlyList<string> FeatureNames,
    string FeatureHash)
{
    public bool IsValid() => !string.IsNullOrWhiteSpace(SchemaVersion)
        && !string.IsNullOrWhiteSpace(FeatureHash)
        && FeatureNames.Count > 0
        && FeatureNames.All(name => !string.IsNullOrWhiteSpace(name))
        && FeatureNames.Distinct(StringComparer.Ordinal).Count() == FeatureNames.Count;
}

public sealed record ModelArtifactContract(
    string ArtifactId,
    string ModelId,
    string ModelVersion,
    string StrategyFamily,
    StrategyDefinitionContract StrategyDefinition,
    string FeatureSchemaHash,
    string ArtifactHash,
    string EvidenceGrade,
    EvidenceProfileContract EvidenceProfile,
    IReadOnlyList<string> ValidationGates,
    IReadOnlyDictionary<string, ValidationGateEvidenceContract> ValidationEvidence,
    string SupportDomain,
    DateTimeOffset CreationTimestamp)
{
    public bool IsValid() => !string.IsNullOrWhiteSpace(ArtifactId)
        && !string.IsNullOrWhiteSpace(ModelId)
        && !string.IsNullOrWhiteSpace(ModelVersion)
        && ExecutableStrategyFamilies.Contains(StrategyFamily)
        && StrategyDefinition.IsValid()
        && !string.IsNullOrWhiteSpace(FeatureSchemaHash)
        && !string.IsNullOrWhiteSpace(ArtifactHash)
        && !string.IsNullOrWhiteSpace(EvidenceGrade)
        && EvidenceProfile.IsValid()
        && ValidationGates.Count > 0
        && ValidationEvidence.Count > 0
        && !string.IsNullOrWhiteSpace(SupportDomain);

    public bool HasRequiredExecutionGates() =>
        RequiredExecutionGates.All(gate => ValidationGates.Contains(gate, StringComparer.Ordinal) &&
            ValidationEvidence.TryGetValue(gate, out ValidationGateEvidenceContract? evidence) &&
            evidence.GateId == gate && evidence.IsValid() && evidence.Passed);

    private static readonly string[] RequiredExecutionGates = ["R0", "R1", "R2", "R3", "R4", "R5", "R6", "R7", "R11", "R12"];
    private static readonly HashSet<string> ExecutableStrategyFamilies = new(StringComparer.Ordinal)
    {
        "price_volume_directional",
        "weekly_time_series_momentum", "four_week_time_series_momentum",
        "dual_horizon_momentum", "four_week_breakout",
        "donchian_breakout", "moving_average_trend", "bollinger_reversion", "rsi_reversion",
        "volatility_breakout", "regime_ensemble", "volume_confirmed_breakout", "compression_breakout",
        "trend_state"
    };
}

public enum StrategyExecutionKind { Spot, DefinedRiskVertical }

/// <summary>Exact research-approved constraints for a debit vertical lifecycle.</summary>
public sealed record OptionVerticalExecutionPolicyContract(
    int MinimumDaysToExpiry,
    int MaximumDaysToExpiry,
    decimal StrikeBandFraction,
    decimal MaximumDefinedLoss,
    decimal ExitLimitFraction)
{
    public bool IsValid() => MinimumDaysToExpiry > 0 && MaximumDaysToExpiry >= MinimumDaysToExpiry &&
        StrikeBandFraction is > 0 and <= 1 && MaximumDefinedLoss > 0 &&
        ExitLimitFraction is > 0 and <= 1;
}

public sealed record StrategyDefinitionContract(
    string Symbol,
    int BarDurationMinutes,
    int ForecastHorizonMinutes,
    string EntryRuleVersion,
    string SignalType,
    string Parameters,
    ExitPolicyDefinitionContract ExitPolicy)
{
    public bool IsValid() => !string.IsNullOrWhiteSpace(Symbol)
        && BarDurationMinutes > 0
        && ForecastHorizonMinutes > 0
        && ForecastHorizonMinutes % BarDurationMinutes == 0
        && !string.IsNullOrWhiteSpace(EntryRuleVersion)
        && SignalType is "Event" or "State"
        && !string.IsNullOrWhiteSpace(Parameters)
        && ExitPolicy.IsValid()
        && (ExecutionKind == StrategyExecutionKind.Spot
            ? OptionVertical is null
            : ExecutionKind == StrategyExecutionKind.DefinedRiskVertical && OptionVertical?.IsValid() == true);

    public StrategyExecutionKind ExecutionKind { get; init; } = StrategyExecutionKind.Spot;
    public OptionVerticalExecutionPolicyContract? OptionVertical { get; init; }
}

public sealed record ExitPolicyDefinitionContract(
    string PolicyVersion,
    int MaximumHoldingMinutes,
    bool ExitOnThesisInvalidation,
    bool ExitOnRegimeChange)
{
    public bool IsValid() => !string.IsNullOrWhiteSpace(PolicyVersion)
        && MaximumHoldingMinutes > 0;
}

public sealed record ValidationGateEvidenceContract(
    string GateId,
    bool Passed,
    IReadOnlyList<string> EvidenceIds,
    DateTimeOffset EvaluatedAt,
    string Details)
{
    public bool IsValid() => !string.IsNullOrWhiteSpace(GateId)
        && EvidenceIds.Count > 0
        && EvidenceIds.All(id => !string.IsNullOrWhiteSpace(id))
        && !string.IsNullOrWhiteSpace(Details);
}

public sealed record EvidenceProfileContract(
    string EvidenceId,
    string EconomicHypothesis,
    string CounterHypothesis,
    IReadOnlyList<string> PrimaryEvidenceIds,
    string TransferGrade,
    string TransferReason)
{
    public bool IsValid() => !string.IsNullOrWhiteSpace(EvidenceId)
        && !string.IsNullOrWhiteSpace(EconomicHypothesis)
        && !string.IsNullOrWhiteSpace(CounterHypothesis)
        && PrimaryEvidenceIds.Count > 0
        && PrimaryEvidenceIds.All(id => !string.IsNullOrWhiteSpace(id))
        && !string.IsNullOrWhiteSpace(TransferGrade)
        && !string.IsNullOrWhiteSpace(TransferReason);

    public bool IsExecutionEligible() => TransferGrade is "A_Direct" or "B_Close";
}

/// <summary>
/// What a published forecast says about its own reliability, and about the family behind it.
///
/// Three separate questions, kept separate on purpose. <see cref="StandardErrorBps"/> says how
/// wrong today's reading could be. <see cref="HistoricalNetEdgeBps"/> says what the family actually
/// earned after costs in research. Neither substitutes for the other, and a point forecast answers
/// neither. See <see cref="QuantDesk.Domain.Forecasts.ForecastEdge"/> for what went wrong when one
/// number was asked to serve all three.
///
/// <see cref="AssumedRoundTripCostBps"/> is what makes the arithmetic safe across the boundary. The
/// research plane publishes a point forecast already net of the cost *it* assumed, so an execution
/// plane that subtracts cost again charges it twice and rejects everything. Stating the assumption
/// lets execution add it back and substitute its own measured figure -- which is the point, since
/// execution is the only side that can measure what a round trip really costs.
/// </summary>
public sealed record ForecastUncertaintyContract(
    double StandardErrorBps,
    double HistoricalNetEdgeBps,
    double HistoricalNetEdgeStandardErrorBps,
    int HistoricalObservations,
    double AssumedRoundTripCostBps)
{
    public bool IsValid() => double.IsFinite(StandardErrorBps) && StandardErrorBps >= 0
        && double.IsFinite(HistoricalNetEdgeBps)
        && double.IsFinite(HistoricalNetEdgeStandardErrorBps) && HistoricalNetEdgeStandardErrorBps >= 0
        && HistoricalObservations > 0
        && double.IsFinite(AssumedRoundTripCostBps) && AssumedRoundTripCostBps >= 0;
}

public sealed record ForecastSnapshotContract(
    string ExpertId,
    string ModelId,
    string ModelVersion,
    string Instrument,
    DateTimeOffset AsOfTime,
    string ForecastFamily,
    int HorizonMinutes,
    decimal PointForecast,
    string FeatureSchemaHash,
    string ArtifactHash,
    string Status,
    string? ReasonCode)
{
    /// <summary>
    /// The forecast's own uncertainty and its family's demonstrated edge.
    ///
    /// Optional on the wire, and null is refused at the gate rather than read as certainty. A
    /// publisher that omits this has not said the forecast is exact; it has said nothing, and
    /// treating silence as zero error is exactly how a point estimate came to be traded as a fact.
    /// </summary>
    public ForecastUncertaintyContract? Uncertainty { get; init; }

    public bool IsValid() => !string.IsNullOrWhiteSpace(ExpertId)
        && !string.IsNullOrWhiteSpace(ModelId)
        && !string.IsNullOrWhiteSpace(ModelVersion)
        && !string.IsNullOrWhiteSpace(Instrument)
        && HorizonMinutes > 0
        && !string.IsNullOrWhiteSpace(ForecastFamily)
        && !string.IsNullOrWhiteSpace(FeatureSchemaHash)
        && !string.IsNullOrWhiteSpace(ArtifactHash)
        && !string.IsNullOrWhiteSpace(Status)
        && (string.Equals(Status, "valid", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(ReasonCode))
        && (Uncertainty is null || Uncertainty.IsValid());
}

public static class ResearchContractValidator
{
    public static bool HasExactFeatureColumns(
        IReadOnlyList<string> expected,
        IReadOnlyList<string> actual)
        => expected.Count == actual.Count && expected.SequenceEqual(actual, StringComparer.Ordinal);
}
