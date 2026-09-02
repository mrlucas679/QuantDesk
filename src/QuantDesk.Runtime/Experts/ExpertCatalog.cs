using QuantDesk.Domain.Forecasts;

namespace QuantDesk.Runtime.Experts;

public enum ExpertRuntimePlane
{
    Warm,
    SlowContext
}

/// <summary>The executable identity and output family of one documented QuantDesk expert.</summary>
public sealed record ExpertDefinition(
    int Id,
    string Key,
    ForecastType ForecastType,
    ExpertRuntimePlane RuntimePlane,
    bool CanAuthorizeDirection);

/// <summary>
/// Stable registry for every expert in docs/experts. An expert being registered does not make its
/// forecast valid; availability is determined from the evidence and artifact supplied at runtime.
/// </summary>
public static class ExpertCatalog
{
    public static IReadOnlyList<ExpertDefinition> All { get; } =
    [
        new(1, "medium-trend", ForecastType.DirectionalReturn, ExpertRuntimePlane.Warm, true),
        new(2, "intraday-momentum", ForecastType.DirectionalReturn, ExpertRuntimePlane.Warm, true),
        new(3, "reversal-liquidity", ForecastType.DirectionalReturn, ExpertRuntimePlane.Warm, true),
        new(4, "statistical-relative-value", ForecastType.RelativeValue, ExpertRuntimePlane.Warm, false),
        new(5, "lightgbm-weak-signal", ForecastType.DirectionalReturn, ExpertRuntimePlane.Warm, true),
        new(6, "realized-volatility", ForecastType.RealizedVolatility, ExpertRuntimePlane.Warm, false),
        new(7, "hmm-regime", ForecastType.Regime, ExpertRuntimePlane.Warm, false),
        new(8, "option-surface-vrp", ForecastType.OptionSurface, ExpertRuntimePlane.Warm, false),
        new(9, "event-jump", ForecastType.JumpRisk, ExpertRuntimePlane.SlowContext, false),
        new(10, "carry-insurance", ForecastType.RelativeValue, ExpertRuntimePlane.Warm, false),
        new(11, "cross-market-relative-value", ForecastType.RelativeValue, ExpertRuntimePlane.Warm, false),
        new(12, "fundamental-text-context", ForecastType.JumpRisk, ExpertRuntimePlane.SlowContext, false),
        new(13, "liquidity-execution-cost", ForecastType.LiquidityCost, ExpertRuntimePlane.Warm, false),
        new(14, "crypto-trend", ForecastType.DirectionalReturn, ExpertRuntimePlane.Warm, true),
        new(15, "crypto-reversal", ForecastType.DirectionalReturn, ExpertRuntimePlane.Warm, true),
        new(16, "crypto-order-book-imbalance", ForecastType.Microstructure, ExpertRuntimePlane.Warm, false),
        new(17, "cross-crypto-relative-value", ForecastType.RelativeValue, ExpertRuntimePlane.Warm, false),
        new(18, "risk-on-cross-asset-context", ForecastType.Regime, ExpertRuntimePlane.Warm, false)
    ];

    public static IReadOnlyList<ExpertDefinition> For(ForecastType family) =>
        All.Where(expert => expert.ForecastType == family).ToArray();

    public static ExpertDefinition Get(int id) =>
        All.SingleOrDefault(expert => expert.Id == id)
        ?? throw new ArgumentOutOfRangeException(nameof(id), "Expert is not registered.");
}
