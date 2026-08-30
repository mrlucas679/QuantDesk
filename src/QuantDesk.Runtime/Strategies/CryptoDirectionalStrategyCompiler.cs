using System.Diagnostics;
using QuantDesk.Domain.Capabilities;
using QuantDesk.Domain.Forecasts;
using QuantDesk.Domain.Numerics;
using QuantDesk.Domain.Portfolio;
using QuantDesk.Domain.Strategies;
using QuantDesk.Domain.Trading;
using QuantDesk.Runtime.State;

namespace QuantDesk.Runtime.Strategies;

/// <summary>Compiles a validated long-only spot-crypto forecast into a managed candidate.</summary>
public sealed class CryptoDirectionalStrategyCompiler(
    Usd targetNotional,
    double stressLossFraction,
    TimeSpan candidateLifetime,
    TimeSpan maximumHoldingPeriod)
{
    public int Compile(
        in ForecastBundle forecasts,
        in InstrumentSnapshot market,
        PortfolioSnapshot portfolio,
        AccountCapabilities capabilities,
        long nowMonotonicTicks,
        Span<TradeCandidate> destination)
    {
        if (destination.IsEmpty || !capabilities.PaperEnvironment || !capabilities.CryptoTrading)
            return 0;
        if (forecasts.Direction is not DirectionalForecast direction ||
            !ForecastValidity.IsFresh(direction.Metadata, nowMonotonicTicks) ||
            !ForecastValidity.IsCausal(direction.Metadata, forecasts.SourceStateVersion) ||
            direction.Metadata.InstrumentSlot != forecasts.InstrumentSlot ||
            market.StateVersion != forecasts.SourceStateVersion ||
            market.QuoteQuality != Domain.Runtime.DataQuality.Healthy ||
            direction.ExpectedReturnBps <= 0 ||
            !double.IsFinite(direction.ExpectedReturnBps))
            return 0;

        Usd stressLoss = new(targetNotional.Value * (decimal)stressLossFraction);
        destination[0] = new TradeCandidate(
            CandidateId: direction.Metadata.GeneratedEventNs,
            InstrumentSlot: forecasts.InstrumentSlot,
            StrategyId: "crypto-long-momentum-v1",
            RiskBasis: RiskBasis.StressLoss,
            SourceStateVersion: forecasts.SourceStateVersion,
            GeneratedMonotonicTicks: nowMonotonicTicks,
            ValidUntilMonotonicTicks: AddDuration(nowMonotonicTicks, candidateLifetime),
            GrossExpectedPnl: new Usd(targetNotional.Value * (decimal)(direction.ExpectedReturnBps / 10_000d)),
            EstimatedStressLoss: stressLoss,
            Exposure: new EconomicExposure(
                targetNotional, 0, 0, 0, 0, 0, 0, (double)targetNotional.Value,
                stressLoss, new Usd(targetNotional.Value * 0.08m), 0),
            ManagementPlan: new PositionManagementPlan(
                maximumHoldingPeriod,
                ExitOnThesisInvalidation: true,
                ExitOnRegimeChange: true,
                MaximumAdverseLoss: stressLoss,
                MinimumDteToHold: null,
                ExitPolicyVersion: "crypto-long-managed-v1"));
        return 1;
    }

    private static long AddDuration(long now, TimeSpan duration)
    {
        double ticks = duration.TotalSeconds * Stopwatch.Frequency;
        return ticks >= long.MaxValue - now ? long.MaxValue : now + (long)Math.Ceiling(ticks);
    }
}
