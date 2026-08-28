using QuantDesk.Domain.Numerics;

namespace QuantDesk.Domain.Forecasts;

public enum ForecastType
{
    DirectionalReturn,
    RealizedVolatility,
    RelativeValue,
    Regime,
    JumpRisk,
    LiquidityCost,
    OptionSurface,
    Microstructure
}

public enum ForecastStatus
{
    Valid,
    Abstain,
    Stale,
    Failed,
    Invalid
}

public readonly record struct ForecastMetadata(
    int ExpertId,
    int InstrumentSlot,
    ForecastType Type,
    TimeSpan Horizon,
    long GeneratedEventNs,
    long GeneratedMonotonicTicks,
    long ValidUntilMonotonicTicks,
    long SourceStateVersion,
    int ModelVersion,
    ForecastStatus Status);

public readonly record struct DirectionalForecast(
    ForecastMetadata Metadata,
    double ExpectedReturnBps,
    double ReturnVariance,
    Probability ProbabilityUp,
    Probability ProbabilityNeutral,
    Probability ProbabilityDown,
    double CalibrationScore);

public static class ForecastValidity
{
    public static bool IsCausal(in ForecastMetadata metadata, long triggerStateVersion) =>
        metadata.SourceStateVersion <= triggerStateVersion;

    public static bool IsFresh(in ForecastMetadata metadata, long nowMonotonicTicks) =>
        metadata.Status == ForecastStatus.Valid && nowMonotonicTicks <= metadata.ValidUntilMonotonicTicks;
}

