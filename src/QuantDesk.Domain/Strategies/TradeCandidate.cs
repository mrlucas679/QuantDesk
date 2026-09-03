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
    PositionManagementPlan ManagementPlan,
    SignalDirection Direction = SignalDirection.Long)
{
    /// <summary>
    /// Which way the position is taken. Every field above is direction-free on purpose.
    ///
    /// <see cref="GrossExpectedPnl"/> is expected *profit*, not expected price movement: a short
    /// that expects -40 bps of return expects +40 bps of profit, and the risk governor's net-edge
    /// gate subtracts cost from it. Signing it would have refused every short as a negative edge.
    /// <see cref="EconomicExposure.Notional"/> is likewise unsigned -- a short consumes buying power
    /// exactly as a long does. Only the betas carry the sign, because a short's beta genuinely
    /// offsets a long's, and netting is the entire point of measuring beta.
    ///
    /// Long by default so that every existing construction keeps its meaning. Note the deliberate
    /// asymmetry with <c>default(TradeCandidate)</c>, which zeroes the struct and therefore reads
    /// <see cref="SignalDirection.None"/>: a candidate nobody compiled has no direction, and None is
    /// refused downstream rather than traded.
    /// </summary>
    public SignalDirection Direction { get; init; } = Direction;
}

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

