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
        long nowTicks,
        Usd projectedCorrelatedExposure = default)
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

        // What the projected book is actually exposed to, as opposed to how many positions it has.
        //
        // CommonExposureLimit and DuplicateExposure have been in RiskReason since the beginning and
        // nothing ever raised either: the envelope capped notional, stress loss, drawdown, position
        // count and dollar delta, and treated a book of seven positions moving as one exactly like
        // seven independent ones. On 2026-09-02 the lane held seven crypto symbols at 0.709 mean
        // pairwise correlation -- about 1.33 independent bets -- with every configured limit
        // satisfied throughout.
        //
        // The measurement is supplied rather than computed here, in the same way DollarDelta is:
        // the governor stays deterministic and free of data-fetching, and the caller that has the
        // return history does the arithmetic. Passing nothing leaves the check inert, which is why
        // the lane test pins that the lane passes it.
        if (projectedCorrelatedExposure.Value > 0 &&
            projectedCorrelatedExposure > _limits.MaximumCorrelatedExposure)
            return Reject(RiskReason.CommonExposureLimit);

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

