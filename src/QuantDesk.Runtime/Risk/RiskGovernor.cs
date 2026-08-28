using QuantDesk.Domain.Market;
using QuantDesk.Domain.Numerics;
using QuantDesk.Domain.Portfolio;
using QuantDesk.Domain.Risk;
using QuantDesk.Domain.Runtime;
using QuantDesk.Domain.Strategies;
using QuantDesk.Runtime.State;

namespace QuantDesk.Runtime.Risk;

public sealed class RiskGovernor
{
    private readonly RiskLimits _limits;

    public RiskGovernor(RiskLimits limits)
    {
        limits.Validate();
        _limits = limits;
    }

    public RiskDecision Evaluate(
        in TradeCandidate candidate,
        in CostEstimate costs,
        in InstrumentSnapshot market,
        PortfolioSnapshot portfolio,
        bool brokerHealthy,
        bool portfolioReconciled,
        long nowTicks)
    {
        if (!brokerHealthy) return Reject(RiskReason.BrokerUnhealthy);
        if (!portfolioReconciled) return Reject(RiskReason.PortfolioUnreconciled);
        if (nowTicks > candidate.ValidUntilMonotonicTicks) return Reject(RiskReason.CandidateExpired);
        if (market.QuoteQuality != DataQuality.Healthy) return Reject(RiskReason.StaleMarketData);
        if (market.RelativeSpread > _limits.MaximumRelativeSpread) return Reject(RiskReason.SpreadTooWide);
        if ((candidate.GrossExpectedPnl - costs.Total).Value <= 0) return Reject(RiskReason.NegativeNetEdge);
        if (candidate.EstimatedStressLoss > _limits.MaximumStressLossPerTrade) return Reject(RiskReason.TradeLossLimit);
        if (portfolio.DailyPnl.Value <= -_limits.MaximumDailyLoss.Value) return Reject(RiskReason.DailyLossLimit);
        if (portfolio.CampaignPnl.Value <= -_limits.MaximumCampaignLoss.Value) return Reject(RiskReason.CampaignLossLimit);
        if (portfolio.Positions.Count >= _limits.MaximumOpenPositions) return Reject(RiskReason.PositionLimit);
        if (Math.Abs(portfolio.DollarDelta + candidate.Exposure.DollarDelta) > _limits.MaximumAbsDollarDelta)
            return Reject(RiskReason.DeltaLimit);
        if (Math.Abs(portfolio.DollarGamma1Pct + candidate.Exposure.DollarGamma1Pct) > _limits.MaximumAbsDollarGamma1Pct)
            return Reject(RiskReason.GammaLimit);
        if (Math.Abs(portfolio.DollarVega1Vol + candidate.Exposure.DollarVega1Vol) > _limits.MaximumAbsDollarVega1Vol)
            return Reject(RiskReason.VegaLimit);
        if (candidate.Exposure.ShortConvexityScore > _limits.MaximumShortConvexityScore)
            return Reject(RiskReason.TailRiskLimit);

        Usd projectedRisk = portfolio.OpenRisk + portfolio.ReservedRisk + candidate.EstimatedStressLoss;
        if (projectedRisk > _limits.MaximumOpenRisk) return Reject(RiskReason.OpenRiskLimit);
        if (candidate.Exposure.Notional > portfolio.BuyingPower) return Reject(RiskReason.BuyingPowerLimit);

        return new RiskDecision(
            true,
            RiskReason.Approved,
            candidate.EstimatedStressLoss,
            candidate.Exposure.Notional);
    }

    private static RiskDecision Reject(RiskReason reason) =>
        new(false, reason, Usd.Zero, Usd.Zero);
}

