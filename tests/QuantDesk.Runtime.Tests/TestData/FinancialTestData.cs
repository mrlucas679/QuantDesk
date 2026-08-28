using QuantDesk.Domain.Numerics;
using QuantDesk.Domain.Portfolio;
using QuantDesk.Domain.Risk;
using QuantDesk.Domain.Runtime;
using QuantDesk.Domain.Strategies;
using QuantDesk.Domain.Trading;
using QuantDesk.Runtime.State;

namespace QuantDesk.Runtime.Tests.TestData;

internal static class FinancialTestData
{
    public static PortfolioSnapshot Portfolio(
        decimal buyingPower = 10_000,
        decimal openRisk = 0,
        decimal reservedRisk = 0) => new(
        Version: 0,
        Equity: new Usd(10_000),
        Cash: new Usd(10_000),
        BuyingPower: new Usd(buyingPower),
        OpenRisk: new Usd(openRisk),
        ReservedRisk: new Usd(reservedRisk),
        DailyPnl: Usd.Zero,
        CampaignPnl: Usd.Zero,
        DollarDelta: 0,
        DollarGamma1Pct: 0,
        DollarVega1Vol: 0,
        DollarTheta1Day: 0,
        EquityBetaUsd: 0,
        TechBetaUsd: 0,
        CryptoBetaUsd: 0,
        Positions: []);

    public static RiskLimits Limits(decimal maximumOpenRisk = 2_000) => new(
        MaximumStressLossPerTrade: new Usd(1_000),
        MaximumOpenRisk: new Usd(maximumOpenRisk),
        MaximumDailyLoss: new Usd(1_000),
        MaximumCampaignLoss: new Usd(2_000),
        MaximumOpenPositions: 10,
        MaximumAbsDollarDelta: 100_000,
        MaximumAbsDollarGamma1Pct: 100_000,
        MaximumAbsDollarVega1Vol: 100_000,
        MaximumRelativeSpread: 0.05,
        MaximumShortConvexityScore: 1);

    public static TradeCandidate Candidate(decimal stressLoss = 100, decimal notional = 500) => new(
        CandidateId: 1,
        InstrumentSlot: 0,
        StrategyId: "trend",
        RiskBasis: RiskBasis.StressLoss,
        SourceStateVersion: 1,
        GeneratedMonotonicTicks: 10,
        ValidUntilMonotonicTicks: 100,
        GrossExpectedPnl: new Usd(25),
        EstimatedStressLoss: new Usd(stressLoss),
        Exposure: new EconomicExposure(
            new Usd(notional), 500, 0, 0, 0, 500, 0, 0, new Usd(stressLoss), new Usd(stressLoss), 0),
        ManagementPlan: new PositionManagementPlan(
            TimeSpan.FromHours(1), true, true, new Usd(stressLoss), null, "exit-v1"));

    public static InstrumentSnapshot HealthyMarket() => new(
        InstrumentSlot: 0,
        StateVersion: 1,
        Bid: 100,
        Ask: 101,
        Mid: 100.5,
        RelativeSpread: 0.00995,
        LastTrade: 100.5,
        Vwap: 100,
        IntervalVolume: 1_000,
        OrderBookImbalance: 0,
        QuoteEventNs: 1,
        TradeEventNs: 1,
        OrderBookEventNs: 1,
        LastReceiveTicks: 1,
        QuoteQuality: DataQuality.Healthy,
        TradeQuality: DataQuality.Healthy,
        OrderBookQuality: DataQuality.Healthy);
}
