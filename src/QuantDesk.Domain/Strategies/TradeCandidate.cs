using QuantDesk.Domain.Numerics;
using QuantDesk.Domain.Trading;

namespace QuantDesk.Domain.Strategies;

public sealed record PositionManagementPlan(
    TimeSpan MaximumHoldingPeriod,
    bool ExitOnThesisInvalidation,
    bool ExitOnRegimeChange,
    Usd? MaximumAdverseLoss,
    int? MinimumDteToHold,
    string ExitPolicyVersion);

public readonly record struct EconomicExposure(
    Usd Notional,
    double DollarDelta,
    double DollarGamma1Pct,
    double DollarVega1Vol,
    double DollarTheta1Day,
    double EquityBetaUsd,
    double TechBetaUsd,
    double CryptoBetaUsd,
    Usd GapLoss3Sigma,
    Usd GapLoss5Sigma,
    double ShortConvexityScore);

public readonly record struct TradeCandidate(
    long CandidateId,
    int InstrumentSlot,
    string StrategyId,
    RiskBasis RiskBasis,
    long SourceStateVersion,
    long GeneratedMonotonicTicks,
    long ValidUntilMonotonicTicks,
    Usd GrossExpectedPnl,
    Usd EstimatedStressLoss,
    EconomicExposure Exposure,
    PositionManagementPlan ManagementPlan);

public readonly record struct CostEstimate(
    Usd EntrySpreadCost,
    Usd ExitSpreadCost,
    Usd Fees,
    Usd Slippage,
    Usd AdverseSelection)
{
    /// <summary>
    /// Cost that was measured on real round trips but that the modelled components do not explain.
    ///
    /// This is deliberately not folded into <see cref="Fees"/> or <see cref="Slippage"/>. Measured
    /// implementation shortfall arrives as a single number and does not decompose, so attributing
    /// the remainder to either one would claim a breakdown the measurement cannot support. Keeping
    /// it separate says exactly what is known: this much was charged, and the model does not
    /// account for it.
    /// </summary>
    public Usd MeasuredExcess { get; init; } = Usd.Zero;

    public Usd Total =>
        EntrySpreadCost + ExitSpreadCost + Fees + Slippage + AdverseSelection + MeasuredExcess;
}

