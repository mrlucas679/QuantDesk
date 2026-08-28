using QuantDesk.Domain.Capabilities;
using QuantDesk.Domain.Forecasts;
using QuantDesk.Domain.Numerics;
using QuantDesk.Domain.Portfolio;
using QuantDesk.Domain.Risk;
using QuantDesk.Domain.Strategies;
using QuantDesk.Domain.Trading;
using QuantDesk.Runtime.State;
using System.Diagnostics;

namespace QuantDesk.Runtime.Strategies;

public sealed class DirectionalStrategyCompiler(
    Usd targetNotional,
    double stressLossFraction,
    TimeSpan candidateLifetime)
{
    public int Compile(
        in ForecastBundle forecasts,
        in InstrumentSnapshot market,
        PortfolioSnapshot portfolio,
        AccountCapabilities capabilities,
        long nowMonotonicTicks,
        Span<TradeCandidate> destination)
    {
        if (destination.IsEmpty || !capabilities.PaperEnvironment || !capabilities.EquityTrading)
            return 0;
        if (forecasts.Direction is not DirectionalForecast direction)
            return 0;
        if (!ForecastValidity.IsFresh(direction.Metadata, nowMonotonicTicks) ||
            !ForecastValidity.IsCausal(direction.Metadata, forecasts.SourceStateVersion) ||
            direction.Metadata.InstrumentSlot != forecasts.InstrumentSlot ||
            market.StateVersion != forecasts.SourceStateVersion ||
            market.QuoteQuality != Domain.Runtime.DataQuality.Healthy ||
            direction.ExpectedReturnBps <= 0 ||
            !double.IsFinite(direction.ExpectedReturnBps) ||
            direction.Metadata.Type != ForecastType.DirectionalReturn)
            return 0;

        Usd grossExpectedPnl = new(targetNotional.Value * (decimal)(direction.ExpectedReturnBps / 10_000.0));
        Usd stressLoss = new(targetNotional.Value * (decimal)stressLossFraction);
        destination[0] = new TradeCandidate(
            CandidateId: direction.Metadata.GeneratedEventNs,
            InstrumentSlot: forecasts.InstrumentSlot,
            StrategyId: "directional-equity",
            RiskBasis: RiskBasis.StressLoss,
            SourceStateVersion: forecasts.SourceStateVersion,
            GeneratedMonotonicTicks: nowMonotonicTicks,
            ValidUntilMonotonicTicks: nowMonotonicTicks + ToMonotonicTicks(candidateLifetime),
            GrossExpectedPnl: grossExpectedPnl,
            EstimatedStressLoss: stressLoss,
            Exposure: new EconomicExposure(
                targetNotional,
                targetNotional.Value > 0 ? (double)targetNotional.Value : 0,
                0,
                0,
                0,
                (double)targetNotional.Value,
                0,
                0,
                stressLoss,
                new Usd(targetNotional.Value * 0.04m),
                0),
            ManagementPlan: new PositionManagementPlan(
                candidateLifetime,
                ExitOnThesisInvalidation: true,
                ExitOnRegimeChange: true,
                MaximumAdverseLoss: stressLoss,
                MinimumDteToHold: null,
                ExitPolicyVersion: "directional-v1"));
        return 1;
    }

    private static long ToMonotonicTicks(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
            return 0;
        double ticks = duration.TotalSeconds * Stopwatch.Frequency;
        return ticks >= long.MaxValue ? long.MaxValue : (long)Math.Ceiling(ticks);
    }
}
