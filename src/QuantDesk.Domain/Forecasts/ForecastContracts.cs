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

/// <summary>Common surface used to validate family-specific forecasts without erasing their units.</summary>
public interface ITypedForecast
{
    ForecastMetadata Metadata { get; }

    /// <summary>
    /// How well this family's forecasts have matched outcomes, in [0, 1].
    ///
    /// On the interface rather than on each record, because every family already carries one and
    /// the committee needs to gate on it uniformly. It used to be readable only on the concrete
    /// directional type, so direction was the one family whose calibration was checked -- and
    /// nothing said why. An uncalibrated volatility forecast sizes a position wrongly and an
    /// uncalibrated regime forecast ends one early; different harms from a wrong direction, not
    /// smaller ones.
    ///
    /// A fitted model is not a calibrated one. The fit says the coefficients came from data; this
    /// says the resulting forecasts were checked against what happened.
    /// </summary>
    double CalibrationScore { get; }
}

public readonly record struct DirectionalForecast(
    ForecastMetadata Metadata,
    double ExpectedReturnBps,
    double ReturnVariance,
    Probability ProbabilityUp,
    Probability ProbabilityNeutral,
    Probability ProbabilityDown,
    double CalibrationScore) : ITypedForecast;

/// <summary>A forecast of the future realized variance distribution, never a directional order.</summary>
public readonly record struct VolatilityForecast(
    ForecastMetadata Metadata,
    double ExpectedRealizedVariance,
    double ExpectedAnnualizedVolatility,
    double ForecastVariance,
    double CalibrationScore) : ITypedForecast;

public enum MarketRegime
{
    Unknown,
    LowVolTrend,
    HighVolTrend,
    Range,
    Stress,
    Event
}

/// <summary>Probabilities over mutually exclusive market regimes.</summary>
public readonly record struct RegimeForecast(
    ForecastMetadata Metadata,
    Probability LowVolTrend,
    Probability HighVolTrend,
    Probability Range,
    Probability Stress,
    Probability Event,
    double CalibrationScore) : ITypedForecast
{
    public MarketRegime MostLikely =>
        new[]
        {
            (MarketRegime.LowVolTrend, LowVolTrend.Value),
            (MarketRegime.HighVolTrend, HighVolTrend.Value),
            (MarketRegime.Range, Range.Value),
            (MarketRegime.Stress, Stress.Value),
            (MarketRegime.Event, Event.Value)
        }.MaxBy(item => item.Value).Item1;
}

/// <summary>A forecast for a residual relationship between two instruments.</summary>
public readonly record struct RelativeValueForecast(
    ForecastMetadata Metadata,
    int SecondInstrumentSlot,
    double ExpectedResidualChangeBps,
    double ResidualVariance,
    double HedgeRatio,

    /// <summary>How stable the fitted relationship between the two instruments has been.</summary>
    double RelationshipStability,

    /// <summary>
    /// How well this family's forecasts have matched outcomes.
    ///
    /// The only family that lacked one. Adding it rather than reading RelationshipStability as a
    /// calibration would have been the quiet kind of wrong: a stable relationship whose forecasts
    /// are consistently off scores high on one and low on the other, and the committee would have
    /// gated on the wrong number without anything looking amiss.
    /// </summary>
    double CalibrationScore) : ITypedForecast;

/// <summary>Event-conditioned probability and magnitude of a discontinuous move.</summary>
public readonly record struct JumpRiskForecast(
    ForecastMetadata Metadata,
    Probability JumpProbability,
    double ExpectedAbsoluteMoveBps,
    double ExpectedDownsideMoveBps,
    double CalibrationScore) : ITypedForecast;

/// <summary>Execution cost and capacity forecast for one instrument and horizon.</summary>
public readonly record struct LiquidityCostForecast(
    ForecastMetadata Metadata,
    double ExpectedSpreadBps,
    double ExpectedSlippageBps,
    Probability FillProbability,
    Usd EstimatedSafeNotional,
    double CalibrationScore) : ITypedForecast;

/// <summary>Options-market richness relative to a separately forecast realized distribution.</summary>
public readonly record struct OptionSurfaceForecast(
    ForecastMetadata Metadata,
    double AtmImpliedVariance,
    double ExpectedRealizedVariance,
    double VarianceRiskPremium,
    double Skew,
    double SurfaceQuality,
    double CalibrationScore) : ITypedForecast;

/// <summary>Short-horizon order-flow evidence derived from a causally maintained order book.</summary>
public readonly record struct MicrostructureForecast(
    ForecastMetadata Metadata,
    double OrderBookImbalance,
    double ExpectedReturnBps,
    Probability FillProbability,
    double AdverseSelectionRisk,
    double CalibrationScore) : ITypedForecast;

public static class ForecastValidity
{
    public static bool IsCausal(in ForecastMetadata metadata, long triggerStateVersion) =>
        metadata.SourceStateVersion <= triggerStateVersion;

    public static bool IsFresh(in ForecastMetadata metadata, long nowMonotonicTicks) =>
        metadata.Status == ForecastStatus.Valid && nowMonotonicTicks <= metadata.ValidUntilMonotonicTicks;
}
