using QuantDesk.Domain.Numerics;

namespace QuantDesk.Domain.Risk;

public sealed record RiskLimits(
    Usd MaximumStressLossPerTrade,
    Usd MaximumOpenRisk,
    Usd MaximumDailyLoss,
    Usd MaximumCampaignLoss,
    int MaximumOpenPositions,
    double MaximumAbsDollarDelta,
    double MaximumAbsDollarGamma1Pct,
    double MaximumAbsDollarVega1Vol,
    double MaximumRelativeSpread,
    double MaximumShortConvexityScore,
    Usd MaximumCorrelatedExposure)
{
    public void Validate()
    {
        if (MaximumStressLossPerTrade.Value <= 0 || MaximumOpenRisk.Value <= 0 ||
            MaximumDailyLoss.Value <= 0 || MaximumCampaignLoss.Value <= 0 ||
            MaximumOpenPositions <= 0 || MaximumAbsDollarDelta <= 0 ||
            MaximumAbsDollarGamma1Pct <= 0 || MaximumAbsDollarVega1Vol <= 0 ||
            MaximumRelativeSpread <= 0 || MaximumShortConvexityScore <= 0 ||
            MaximumCorrelatedExposure.Value <= 0)
        {
            throw new InvalidOperationException("Every risk limit must be positive and explicitly bounded.");
        }
    }
}

public enum RiskReason
{
    Approved,
    SystemHalted,
    CandidateExpired,
    StaleMarketData,
    NegativeNetEdge,
    TradeLossLimit,
    OpenRiskLimit,
    DailyLossLimit,
    CampaignLossLimit,
    PositionLimit,
    DeltaLimit,
    GammaLimit,
    VegaLimit,
    ConcentrationLimit,
    CommonExposureLimit,
    SpreadTooWide,
    TailRiskLimit,
    DuplicateExposure,
    BuyingPowerLimit,
    BrokerUnhealthy,
    PortfolioUnreconciled,
    Unknown
}

public readonly record struct RiskDecision(
    bool Approved,
    RiskReason Reason,
    Usd RequiredRiskReservation,
    Usd RequiredCapitalReservation);

