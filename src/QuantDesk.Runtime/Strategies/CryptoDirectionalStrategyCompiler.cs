using System.Diagnostics;
using QuantDesk.Domain.Capabilities;
using QuantDesk.Domain.Contracts;
using QuantDesk.Domain.Forecasts;
using QuantDesk.Domain.Numerics;
using QuantDesk.Domain.Portfolio;
using QuantDesk.Domain.Strategies;
using QuantDesk.Domain.Trading;
using QuantDesk.Runtime.State;

namespace QuantDesk.Runtime.Strategies;

/// <summary>
/// Compiles a validated long-only directional forecast into a managed candidate.
///
/// Named for crypto because that is all it used to compile, and the assumption was baked in twice
/// over rather than stated. It refused to produce a candidate unless the account had *crypto*
/// permission, whatever the instrument was, and it booked the whole position as crypto beta. Point
/// the lane at SPY and both are wrong in ways that do not announce themselves: the trade is gated
/// on a permission it does not need, and the risk governor sees an equity position as pure crypto
/// exposure, so equity beta limits never bind and crypto limits bind against something that is not
/// crypto.
///
/// The asset class is supplied per call rather than per instance, because one lane now compiles for
/// several instruments at once. Holding it on the compiler was correct only while every candidate
/// came from the same venue; with stocks and crypto in one lane it would silently apply the first
/// symbol's class to all of them, which is the same defect one level up.
/// </summary>
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
        TradedAssetClass assetClass,
        Span<TradeCandidate> destination) => Compile(
            forecasts, market, portfolio, capabilities, nowMonotonicTicks, assetClass,
            "crypto-long-momentum-v1", destination);

    public int Compile(
        in ForecastBundle forecasts,
        in InstrumentSnapshot market,
        PortfolioSnapshot portfolio,
        AccountCapabilities capabilities,
        long nowMonotonicTicks,
        TradedAssetClass assetClass,
        string strategyFamily,
        StrategyDefinitionContract definition,
        Span<TradeCandidate> destination)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!definition.IsValid()) return 0;
        int count = Compile(
            forecasts, market, portfolio, capabilities, nowMonotonicTicks, assetClass,
            strategyFamily, destination);
        if (count == 0) return 0;
        TradeCandidate compiled = destination[0];
        destination[0] = compiled with
        {
            ManagementPlan = new PositionManagementPlan(
                TimeSpan.FromMinutes(definition.ExitPolicy.MaximumHoldingMinutes),
                definition.ExitPolicy.ExitOnThesisInvalidation,
                definition.ExitPolicy.ExitOnRegimeChange,
                compiled.EstimatedStressLoss,
                MinimumDteToHold: null,
                definition.ExitPolicy.PolicyVersion)
        };
        return 1;
    }

    public int Compile(
        in ForecastBundle forecasts,
        in InstrumentSnapshot market,
        PortfolioSnapshot portfolio,
        AccountCapabilities capabilities,
        long nowMonotonicTicks,
        TradedAssetClass assetClass,
        string strategyFamily,
        Span<TradeCandidate> destination)
    {
        if (destination.IsEmpty || string.IsNullOrWhiteSpace(strategyFamily) ||
            !capabilities.PaperEnvironment || !IsPermitted(capabilities, assetClass))
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
            StrategyId: strategyFamily,
            RiskBasis: RiskBasis.StressLoss,
            SourceStateVersion: forecasts.SourceStateVersion,
            GeneratedMonotonicTicks: nowMonotonicTicks,
            ValidUntilMonotonicTicks: AddDuration(nowMonotonicTicks, candidateLifetime),
            GrossExpectedPnl: new Usd(targetNotional.Value * (decimal)(direction.ExpectedReturnBps / 10_000d)),
            EstimatedStressLoss: stressLoss,
            Exposure: BookExposure(stressLoss, assetClass),
            ManagementPlan: new PositionManagementPlan(
                maximumHoldingPeriod,
                ExitOnThesisInvalidation: true,
                ExitOnRegimeChange: true,
                MaximumAdverseLoss: stressLoss,
                MinimumDteToHold: null,
                ExitPolicyVersion: ExitPolicyVersionFor(assetClass)));
        return 1;
    }

    /// <summary>The permission this instrument actually requires from the venue.</summary>
    private static bool IsPermitted(AccountCapabilities capabilities, TradedAssetClass assetClass) => assetClass switch
    {
        TradedAssetClass.SpotCrypto => capabilities.CryptoTrading,
        TradedAssetClass.UsEquity => capabilities.EquityTrading,
        TradedAssetClass.UsEquityOption => capabilities.OptionsTrading,
        _ => false,
    };

    /// <summary>
    /// Books the notional against the beta that belongs to it.
    ///
    /// The whole position used to land in CryptoBetaUsd regardless. That is not a labelling
    /// nicety: the risk governor reads these fields to enforce per-factor exposure limits, so an
    /// equity booked as crypto leaves the equity limit unused and consumes a crypto limit against
    /// exposure the portfolio does not have.
    /// </summary>
    private EconomicExposure BookExposure(Usd stressLoss, TradedAssetClass assetClass)
    {
        double notional = (double)targetNotional.Value;
        double equityBeta = assetClass is TradedAssetClass.SpotCrypto ? 0d : notional;
        double cryptoBeta = assetClass is TradedAssetClass.SpotCrypto ? notional : 0d;

        return new EconomicExposure(
            targetNotional, 0, 0, 0, 0, equityBeta, 0, cryptoBeta,
            stressLoss, new Usd(targetNotional.Value * 0.08m), 0);
    }

    private static string ExitPolicyVersionFor(TradedAssetClass assetClass) =>
        assetClass is TradedAssetClass.SpotCrypto
            ? "crypto-long-managed-v1"
            : "equity-long-managed-v1";

    private static long AddDuration(long now, TimeSpan duration)
    {
        double ticks = duration.TotalSeconds * Stopwatch.Frequency;
        return ticks >= long.MaxValue - now ? long.MaxValue : now + (long)Math.Ceiling(ticks);
    }
}
