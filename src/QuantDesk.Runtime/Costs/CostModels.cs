using QuantDesk.Domain.Numerics;
using QuantDesk.Domain.Portfolio;
using QuantDesk.Domain.Strategies;
using QuantDesk.Runtime.State;

namespace QuantDesk.Runtime.Costs;

public interface ICostModel
{
    CostEstimate Estimate(in TradeCandidate candidate, in InstrumentSnapshot market);
}

public sealed class EquityCostModel(BasisPoints fee, BasisPoints slippage) : ICostModel
{
    public CostEstimate Estimate(in TradeCandidate candidate, in InstrumentSnapshot market)
    {
        Usd spread = new(candidate.Exposure.Notional.Value * (decimal)Math.Max(0, market.RelativeSpread * 0.5));
        Usd fees = new(candidate.Exposure.Notional.Value * (decimal)fee.Fraction);
        Usd slip = new(candidate.Exposure.Notional.Value * (decimal)slippage.Fraction);
        return new(spread, spread, fees, slip, Usd.Zero);
    }
}

public sealed class CryptoCostModel(BasisPoints fee, BasisPoints slippage) : ICostModel
{
    public CostEstimate Estimate(in TradeCandidate candidate, in InstrumentSnapshot market)
    {
        Usd spread = new(candidate.Exposure.Notional.Value * (decimal)Math.Max(0, market.RelativeSpread * 0.5));
        Usd fees = new(candidate.Exposure.Notional.Value * (decimal)fee.Fraction);
        Usd slip = new(candidate.Exposure.Notional.Value * (decimal)slippage.Fraction);
        return new(spread, spread, fees, slip, Usd.Zero);
    }
}

public sealed class OptionCostModel(BasisPoints fee, BasisPoints slippage) : ICostModel
{
    public CostEstimate Estimate(in TradeCandidate candidate, in InstrumentSnapshot market)
    {
        Usd spread = new(candidate.Exposure.Notional.Value * (decimal)Math.Max(0, market.RelativeSpread));
        Usd fees = new(candidate.Exposure.Notional.Value * (decimal)fee.Fraction);
        Usd slip = new(candidate.Exposure.Notional.Value * (decimal)slippage.Fraction);
        return new(spread, spread, fees, slip, Usd.Zero);
    }
}

public static class NetEconomics
{
    public static Usd NetExpectedPnl(in TradeCandidate candidate, in CostEstimate costs) =>
        candidate.GrossExpectedPnl - costs.Total;
}
