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
    public Usd Total => EntrySpreadCost + ExitSpreadCost + Fees + Slippage + AdverseSelection;
}

