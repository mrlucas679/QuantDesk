using QuantDesk.Domain.Forecasts;
using QuantDesk.Domain.Numerics;

namespace QuantDesk.Domain.Scoring;

public enum ForecastScoreMetric
{
    RootMeanSquaredError,
    Brier,
    QLike
}

public enum ScoreEvidenceStatus
{
    Scored,
    InsufficientEvidence
}

public sealed record ExpertForecastOutcome(
    long EpisodeId,
    long ForecastId,
    int ExpertId,
    ForecastType ForecastType,
    double PredictedValue,
    double ObservedValue,
    double? PredictedProbability,
    bool? EventOccurred,
    string Regime)
{
    public bool IsValid() => EpisodeId > 0
        && ForecastId > 0
        && ExpertId >= 0
        && Enum.IsDefined(ForecastType)
        && double.IsFinite(PredictedValue)
        && double.IsFinite(ObservedValue)
        && (PredictedProbability is null ||
            (double.IsFinite(PredictedProbability.Value) && PredictedProbability.Value is >= 0 and <= 1))
        && !string.IsNullOrWhiteSpace(Regime);
}

public sealed record ExpertForecastScore(
    int ExpertId,
    ForecastType ForecastType,
    string Regime,
    ForecastScoreMetric PrimaryMetric,
    ScoreEvidenceStatus Status,
    int SampleCount,
    int IndependentEpisodeCount,
    double? PrimaryLoss,
    double? MeanAbsoluteError,
    double? RootMeanSquaredError,
    double? BrierScore,
    double? QLike,
    double? DirectionalAccuracy,
    double? CalibrationError);

public sealed record EpisodeAttributionInput(
    long EpisodeId,
    Usd PaperPnl,
    Usd AlphaOrForecastContribution,
    Usd StrategyExpressionContribution,
    Usd SpreadCost,
    Usd SlippageCost,
    Usd FeeCost,
    Usd TimingCost,
    Usd SizingRiskContribution,
    Usd FactorStyleContribution,
    Usd TailRiskContribution,
    Usd CrowdingContribution,
    Usd AdditionalRealismCost);

public sealed record EpisodeAttributionScore(
    long EpisodeId,
    Usd PaperPnl,
    Usd RealismAdjustedPnl,
    Usd ForecastContribution,
    Usd StrategyExpressionContribution,
    Usd ExecutionContribution,
    Usd TimingContribution,
    Usd RiskSizingContribution,
    Usd FactorStyleContribution,
    Usd TailRiskContribution,
    Usd CrowdingContribution,
    Usd Residual);
