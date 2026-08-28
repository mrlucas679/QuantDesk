using QuantDesk.Domain.Numerics;
using QuantDesk.Domain.Strategies;

namespace QuantDesk.Domain.Portfolio;

public sealed record PositionSnapshot(
    int InstrumentSlot,
    decimal Quantity,
    decimal AveragePrice,
    Usd RealizedPnl,
    Usd UnrealizedPnl,
    EconomicExposure Exposure,
    string StrategyId,
    string ExitPolicyVersion,
    long OpenedEventNs);

public sealed record PortfolioSnapshot(
    long Version,
    Usd Equity,
    Usd Cash,
    Usd BuyingPower,
    Usd OpenRisk,
    Usd ReservedRisk,
    Usd DailyPnl,
    Usd CampaignPnl,
    double DollarDelta,
    double DollarGamma1Pct,
    double DollarVega1Vol,
    double DollarTheta1Day,
    double EquityBetaUsd,
    double TechBetaUsd,
    double CryptoBetaUsd,
    IReadOnlyList<PositionSnapshot> Positions);

public readonly record struct NormalizedFill(
    string ClientOrderId,
    string BrokerOrderId,
    int InstrumentSlot,
    Trading.OrderSide Side,
    decimal Quantity,
    decimal Price,
    long EventUnixNanoseconds,
    string FillId);

public sealed record VirtualStrategyLot(
    long LotId,
    int InstrumentSlot,
    string StrategyId,
    decimal Quantity,
    decimal EntryPrice,
    long EpisodeId,
    int PolicyVersion,
    int[] ForecastIds);

public readonly record struct TradeAttribution(
    Usd AlphaOrForecastContribution,
    Usd StrategyExpressionContribution,
    Usd SpreadCost,
    Usd SlippageCost,
    Usd FeeCost,
    Usd TimingCost,
    Usd SizingRiskContribution,
    Usd Residual);

public sealed record MarketEpisode(
    long EpisodeId,
    int PrimaryInstrumentSlot,
    long StartEventNs,
    long EndEventNs,
    IReadOnlyList<long> ForecastIds,
    IReadOnlyList<long> CandidateIds,
    IReadOnlyList<string> ClientOrderIds);
