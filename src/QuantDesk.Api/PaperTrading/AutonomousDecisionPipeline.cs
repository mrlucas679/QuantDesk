using QuantDesk.Alpaca.MarketData;
using QuantDesk.Domain.Capabilities;
using QuantDesk.Domain.Contracts;
using QuantDesk.Domain.Experts;
using QuantDesk.Domain.Forecasts;
using QuantDesk.Domain.Market;
using QuantDesk.Domain.Numerics;
using QuantDesk.Domain.Portfolio;
using QuantDesk.Domain.Risk;
using QuantDesk.Domain.Strategies;
using QuantDesk.Runtime.Actionability;
using QuantDesk.Runtime.Costs;
using QuantDesk.Runtime.Experts;
using QuantDesk.Runtime.Risk;
using QuantDesk.Runtime.State;
using QuantDesk.Runtime.Strategies;
using QuantDesk.Runtime.Time;

namespace QuantDesk.Api.PaperTrading;

public sealed record AutonomousPipelineDecision(
    bool Approved,
    string Reason,
    TradeCandidate? Candidate,
    CostEstimate? Costs,
    RiskDecision? Risk,
    CommitteeDecision? Committee,
    InstrumentSnapshot? Market);

/// <summary>Owns the deterministic evidence-to-risk-decision path for autonomous spot crypto.</summary>
public sealed class AutonomousDecisionPipeline(
    MarketStateStore marketState,
    ExpertCommittee committee,
    CryptoDirectionalStrategyCompiler compiler,
    AssetClassPricing pricing,
    ActionabilityGate actionability,
    RiskGovernor riskGovernor,
    IRuntimeClock clock,
    ILogger<AutonomousDecisionPipeline> logger)
{
    /// <summary>
    /// What an unverified experimental candidate is called.
    ///
    /// Named for the mechanism rather than the venue. It used to default to
    /// "crypto-long-momentum-v1" whatever was being traded, so an equity position was recorded,
    /// attributed, and reconciled under a crypto strategy name -- harmless to execution and
    /// actively misleading to anyone reading the audit trail afterwards.
    /// </summary>
    private const string DefaultStrategyFamily = "directional-long-momentum-v1";

    private const int MediumTrendExpertId = 14;
    private const int ShortMomentumExpertId = 15;

    public AutonomousPipelineDecision Evaluate(
        int instrumentSlot,
        OpportunityRoute route,
        DirectionalMarketEvidence evidence,
        PortfolioSnapshot portfolio,
        bool brokerHealthy,
        bool portfolioReconciled,
        AccountCapabilities capabilities,
        double? verifiedForecastBps = null,
        string? verifiedStrategyFamily = null,
        StrategyDefinitionContract? verifiedStrategyDefinition = null,
        ForecastUncertaintyContract? forecastUncertainty = null,
        double? allInCostUpperBoundBps = null)
    {
        // Capabilities are required, not defaulted.
        //
        // This parameter was optional, falling back to a literal
        // `new AccountCapabilities(true, false, true, false, null)` when a caller passed nothing.
        // That fallback asserted PAPER without checking and granted crypto permission without
        // asking the account — a permission the venue is supposed to grant, invented locally. A
        // caller that forgot to thread the probe through got a silent yes rather than a failure.
        //
        // Permission must come from the live probe or the cycle must not run. There is no safe
        // default for "may this account trade this asset class", so there is no default.
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(route);

        // Priced for this instrument, not for whatever the lane was configured with at startup.
        CryptoResearchGate researchGate = pricing.GateFor(route);
        ICostModel costs = pricing.CostsFor(route);
        AccountCapabilities effectiveCapabilities = capabilities;
        if (verifiedForecastBps is null)
        {
            CryptoResearchDecision research = researchGate.Evaluate(evidence);
            if (!research.Approved)
            {
                logger.LogInformation(
                    "Experimental decision abstained: reason={Reason}, mediumMomentumBps={Medium}, shortMomentumBps={Short}, spreadBps={Spread}, lookbackBars={Lookback}, quoteAge=live",
                    research.Reason, research.MediumMomentumBps, research.ShortMomentumBps,
                    research.SpreadBps, research.LookbackBars);
                return Reject(research.Reason);
            }
        }
        else if (!double.IsFinite(verifiedForecastBps.Value) || verifiedForecastBps.Value <= 0)
        {
            return Reject("VerifiedForecastNotPositive");
        }
        else if (allInCostUpperBoundBps is { } costBound)
        {
            // A point forecast was being compared against a modelled cost as though both were
            // facts. Three different questions were riding on that one number: what the model says
            // now, what the family has historically earned net of costs, and how wrong the current
            // reading could be. Separating them means a large noisy reading from a family that has
            // never made money is refused, which the point comparison could not do.
            var edge = new ForecastEdge(
                verifiedForecastBps.Value,
                forecastUncertainty?.StandardErrorBps,
                forecastUncertainty?.HistoricalNetEdgeBps,
                forecastUncertainty?.HistoricalNetEdgeStandardErrorBps,
                forecastUncertainty?.HistoricalObservations,
                forecastUncertainty?.AssumedRoundTripCostBps);

            ForecastEdgeAssessment assessment = ForecastEdgeAssessment.Evaluate(edge, costBound);
            if (!assessment.Tradable)
            {
                logger.LogInformation(
                    "Verified forecast refused: reason={Reason}, signal={Signal}bps, " +
                    "signalLowerBound={SignalBound}, historicalLowerBound={HistoricalBound}, " +
                    "costUpperBound={Cost}bps.",
                    assessment.Reason, verifiedForecastBps.Value,
                    assessment.CurrentSignalLowerBoundBps, assessment.HistoricalNetEdgeLowerBoundBps,
                    costBound);
                return Reject(assessment.Reason);
            }
        }

        long nowTicks = clock.MonotonicTimestamp;
        long eventNs = clock.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L;
        ValidationResult applied = marketState.Apply(new QuoteEvent(
            eventNs, instrumentSlot, (double)evidence.Bid, (double)evidence.Ask,
            1, 1, eventNs, nowTicks, eventNs));
        if (!applied.IsValid) return Reject("StaleMarketData");
        InstrumentSnapshot market = marketState.Snapshot(instrumentSlot);

        long validUntil = nowTicks + (long)(TimeSpan.FromMinutes(5).TotalSeconds * System.Diagnostics.Stopwatch.Frequency);
        ExpertVote[] votes = verifiedForecastBps is double modelBps
            ? [CreateVote(1001, instrumentSlot, modelBps, market.StateVersion, eventNs, nowTicks, validUntil)]
            :
            [
                CreateVote(MediumTrendExpertId, instrumentSlot, ReturnBps(evidence.Closes[^13], evidence.Closes[^1]), market.StateVersion, eventNs, nowTicks, validUntil),
                CreateVote(ShortMomentumExpertId, instrumentSlot, ReturnBps(evidence.Closes[^4], evidence.Closes[^1]), market.StateVersion, eventNs + 1, nowTicks, validUntil)
            ];
        CommitteeDecision committeeDecision = committee.Evaluate(
            instrumentSlot, votes, nowTicks, market.StateVersion);
        if (!committeeDecision.Actionable)
            return new(false, "InsufficientExpertAvailability", null, null, null, committeeDecision, market);

        DirectionalForecast aggregate = CreateForecast(
            expertId: 1000,
            instrumentSlot,
            committeeDecision.ExpectedReturnBps,
            committeeDecision.AgreementScore,
            market.StateVersion,
            eventNs + 2,
            nowTicks,
            validUntil);
        var bundle = new ForecastBundle(instrumentSlot, market.StateVersion, aggregate);
        var candidates = new TradeCandidate[1];
        int count = verifiedStrategyDefinition is null
            ? compiler.Compile(
                bundle, market, portfolio,
                effectiveCapabilities,
                nowTicks, route.AssetClass, verifiedStrategyFamily ?? DefaultStrategyFamily,
                candidates)
            : compiler.Compile(
                bundle, market, portfolio,
                effectiveCapabilities,
                nowTicks, route.AssetClass, verifiedStrategyFamily ?? DefaultStrategyFamily,
                verifiedStrategyDefinition, candidates);
        if (count == 0) return Reject("NoOpportunity", committeeDecision, market);

        TradeCandidate candidate = candidates[0];
        CostEstimate estimate = costs.Estimate(candidate, market);
        ActionabilityAssessment actionable = actionability.Evaluate(candidate, estimate, market);
        if (!actionable.Actionable)
            return new(false, actionable.Reason.ToString(), candidate, estimate, null, committeeDecision, market);

        RiskDecision risk = riskGovernor.Evaluate(
            candidate, estimate, market, portfolio, brokerHealthy, portfolioReconciled, nowTicks);
        return risk.Approved
            ? new(true, "Approved", candidate, estimate, risk, committeeDecision, market)
            : new(false, risk.Reason.ToString(), candidate, estimate, risk, committeeDecision, market);
    }

    private static ExpertVote CreateVote(
        int expertId, int slot, double expectedReturnBps, long stateVersion,
        long eventNs, long nowTicks, long validUntil) =>
        new(expertId, CreateForecast(expertId, slot, expectedReturnBps, 0.75, stateVersion,
            eventNs, nowTicks, validUntil), 0.5);

    private static DirectionalForecast CreateForecast(
        int expertId, int slot, double expectedReturnBps, double calibration,
        long stateVersion, long eventNs, long nowTicks, long validUntil)
    {
        double up = Math.Clamp(0.5 + expectedReturnBps / 1_000d, 0.05, 0.9);
        double down = Math.Clamp(1 - up - 0.1, 0.05, 0.9);
        return new DirectionalForecast(
            new ForecastMetadata(expertId, slot, ForecastType.DirectionalReturn, TimeSpan.FromMinutes(5),
                eventNs, nowTicks, validUntil, stateVersion, 1, ForecastStatus.Valid),
            expectedReturnBps, 1, new Probability(up), new Probability(0.1), new Probability(down), calibration);
    }

    private static double ReturnBps(decimal start, decimal end) =>
        start <= 0 ? double.NegativeInfinity : (double)((end / start - 1m) * 10_000m);

    private static AutonomousPipelineDecision Reject(
        string reason, CommitteeDecision? committee = null, InstrumentSnapshot? market = null) =>
        new(false, reason, null, null, null, committee, market);
}
