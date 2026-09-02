using QuantDesk.Alpaca.MarketData;
using QuantDesk.Domain.Capabilities;
using QuantDesk.Domain.Contracts;
using QuantDesk.Domain.Experts;
using QuantDesk.Domain.Forecasts;
using QuantDesk.Domain.Trading;
using QuantDesk.Domain.Market;
using QuantDesk.Domain.Numerics;
using QuantDesk.Domain.Portfolio;
using QuantDesk.Domain.Risk;
using QuantDesk.Domain.Strategies;
using QuantDesk.Runtime.Actionability;
using QuantDesk.Runtime.Costs;
using QuantDesk.Runtime.Experts;
using QuantDesk.Runtime.Indicators;
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
    StrategyRotation rotation,
    ActionabilityGate actionability,
    RiskGovernor riskGovernor,
    IRuntimeClock clock,
    ILogger<AutonomousDecisionPipeline> logger,
    Func<TradedAssetClass, IReadOnlyList<SignalStrategy>>? tradableStrategies = null)
{
    /// <summary>
    /// The strategies this pipeline may open a position with.
    ///
    /// Injectable so a test can state which strategies it is exercising rather than depending on
    /// whichever ones currently survive the evidence filter -- a test about the cost gate should
    /// not start failing because a strategy's measured mean moved.
    /// </summary>
    private readonly Func<TradedAssetClass, IReadOnlyList<SignalStrategy>> _tradable =
        tradableStrategies ?? SignalStrategies.Tradable;

    /// <summary>
    /// What an unverified experimental candidate is called.
    ///
    /// Named for the mechanism rather than the venue. It used to default to
    /// "crypto-long-momentum-v1" whatever was being traded, so an equity position was recorded,
    /// attributed, and reconciled under a crypto strategy name -- harmless to execution and
    /// actively misleading to anyone reading the audit trail afterwards.
    /// </summary>
    private const string DefaultStrategyFamily = "directional-long-momentum-v1";

    /// <summary>
    /// Asks every strategy for this asset class whether it fires on the latest bar.
    ///
    /// Returns null when the evidence cannot support the indicators -- a series without highs, lows
    /// and volumes, or one too short for the slowest indicator to have warmed up. Declining is the
    /// only honest answer there: an indicator seeded on too little history returns a number that
    /// looks valid and is wrong.
    /// </summary>
    private StrategyEvaluation SelectStrategy(
        DirectionalMarketEvidence evidence,
        OpportunityRoute route,
        IReadOnlyDictionary<string, int> openByMechanism)
    {
        IndicatorSet? indicators = evidence.HasFullBars
            ? IndicatorSet.Build(evidence.Closes, evidence.Highs, evidence.Lows, evidence.Volumes)
            : null;

        // Only strategies not already measured to lose. See SignalStrategies.IsKnownToLose: being
        // unproven is worth paying a little to resolve, being demonstrably unprofitable is not.
        IReadOnlyList<SignalStrategy> available = _tradable(route.AssetClass);
        if (indicators is not null)
            return rotation.Select(available, indicators, openByMechanism, MechanismCap(available));

        // Closes only, or too little history for the slower indicators. Rather than fall silent,
        // fall back to the families that genuinely need nothing else -- which is what the lane
        // traded before any of this existed. Going quiet here would be the worse failure: a feed
        // that briefly returns short history would stop the lane without any signal that it had.
        IReadOnlyList<SignalStrategy> closesOnly =
            [.. SignalStrategies.ClosesOnly(route.AssetClass)
                .Where(item => _tradable(route.AssetClass).Any(t => t.Id == item.Id))];
        return closesOnly.Count == 0
            ? StrategyEvaluation.None
            : rotation.Select(closesOnly, CloseOnlySet(evidence), openByMechanism, MechanismCap(available));
    }

    /// <summary>
    /// An indicator set carrying closes alone, with the bar's high and low taken as the close.
    ///
    /// Only the closes-only strategies are ever evaluated against it, so nothing reads the
    /// synthesised high or low -- they exist to satisfy the shape, not to be used. A rule that
    /// needed a true range would read zero here, which is why none of those rules is offered.
    /// </summary>
    private static IndicatorSet CloseOnlySet(DirectionalMarketEvidence evidence)
    {
        IReadOnlyList<decimal> zeroVolume = [.. evidence.Closes.Select(_ => 0m)];
        return IndicatorSet.Build(evidence.Closes, evidence.Closes, evidence.Closes, zeroVolume)
            ?? IndicatorSet.Unwarmed(evidence.Closes);
    }

    /// <summary>
    /// The expert that speaks for whichever strategy fired.
    ///
    /// One vote rather than two, because there is one hypothesis: the strategy's. The two momentum
    /// experts remain for the path where no strategy layer runs, and their disagreement is
    /// meaningful there because they are genuinely two readings of the same mechanism.
    /// </summary>
    /// <summary>
    /// How many concurrent positions one mechanism may hold.
    ///
    /// Half the mechanisms in play, rounded up, which leaves the other half room to act when their
    /// conditions arrive. A tighter cap would starve whichever mechanism the market currently
    /// favours; a looser one lets it take everything, which is the state this exists to prevent.
    /// </summary>
    private static int MechanismCap(IReadOnlyList<SignalStrategy> available)
    {
        int mechanisms = available.Select(item => item.Mechanism).Distinct(StringComparer.Ordinal).Count();
        return Math.Max(1, (int)Math.Ceiling(mechanisms / 2.0));
    }

    private const int StrategyExpertId = 16;

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
        IReadOnlyDictionary<string, int>? openPositionsByMechanism = null,
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
        CostViabilityGate viabilityGate = pricing.ViabilityFor(route);
        ICostModel costs = pricing.CostsFor(route);
        AccountCapabilities effectiveCapabilities = capabilities;
        StrategySelection? selection = null;
        double expectedMoveBps = 0d;
        IReadOnlyDictionary<string, int> openByMechanism =
            openPositionsByMechanism ?? new Dictionary<string, int>(StringComparer.Ordinal);
        if (verifiedForecastBps is null)
        {
            // Strategies first, then the cost hurdle.
            //
            // The order used to be the other way round, and it quietly made most of the strategy
            // set unreachable. The gate's first test is that recent momentum is positive and
            // exceeds the round trip -- which is the momentum strategy's own entry condition, not a
            // property of trading in general. Running it in front of everything meant a
            // mean-reversion rule could only fire when prices were already rising, so buying a dip
            // required the dip not to have happened. Nine of thirteen strategies could never have
            // opened a position.
            StrategyEvaluation evaluation = SelectStrategy(evidence, route, openByMechanism);
            selection = evaluation.Selection;
            if (selection is null)
            {
                // No signal, nothing left worth trading, or nothing that could be asked. All three
                // look identical from outside and mean entirely different things to an operator: a
                // quiet market, an asset class whose every strategy is measured to lose, and a
                // strategy book that threw. The last one used to be reported as the first, so a
                // broken rule set would have read as patience.
                if (evaluation.Faulted)
                {
                    return Reject(
                        $"StrategyEvaluationFaulted:{evaluation.Faults[0].StrategyId}");
                }

                return Reject(_tradable(route.AssetClass).Count == 0
                    ? "AllStrategiesKnownToLose"
                    : "NoStrategySignal");
            }

            // The hurdle a strategy of any direction has to clear: does this instrument move enough
            // over the holding period to pay for the round trip at all? That is a necessary
            // condition for every mechanism and specific to none of them, where trailing momentum
            // is specific to one.
            CostViability viability = viabilityGate.Evaluate(evidence, route);
            expectedMoveBps = (double)viability.ExpectedMoveBps;
            if (!viability.Viable)
            {
                logger.LogInformation(
                    "Strategy {Strategy} fired on {Instrument} but the instrument cannot pay its costs: " +
                    "expected move {Move:0.0} bps against a {Hurdle:0.0} bps hurdle.",
                    selection.Strategy.Id, instrumentSlot, viability.ExpectedMoveBps, viability.HurdleBps);
                return Reject(viability.Reason);
            }

            logger.LogInformation(
                "Strategy {Strategy} admitted {Instrument}; also firing: {Agreeing}. " +
                "Research mean {Mean:0.0} bps, lower bound {Lower:0.0} bps, qualification {Qualification}.",
                selection.Strategy.Id, instrumentSlot,
                selection.AlsoFired.Count == 0 ? "none" : string.Join(", ", selection.AlsoFired),
                selection.Strategy.ResearchMeanNetBps, selection.Strategy.ResearchLowerBoundBps,
                selection.Strategy.Qualification);
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
        // What the firing strategy expects, not what price has just done.
        //
        // These votes used to be built from trailing momentum whatever had fired, and everything
        // downstream reads them: the committee refuses when they disagree, and their weighted value
        // becomes the gross expected P&L the actionability gate charges costs against. So a
        // mean-reversion strategy -- which by construction fires when momentum is *negative* --
        // passed the strategy layer and was then refused as InsufficientExpertAvailability or
        // NegativeNetEdge. Nine of thirteen strategies still could not trade; they simply failed a
        // step later than before, with a reason that pointed at the wrong thing.
        //
        // A strategy that fires expects the instrument to move its own way by about as much as the
        // instrument typically moves. That magnitude is measured; the direction is the strategy's
        // hypothesis, and an explicitly unqualified one. Nothing here asserts the strategy is right
        // -- it asserts only that the move it is betting on is large enough to be worth the costs,
        // which is what this gate is for.
        ExpertVote[] votes = verifiedForecastBps is double modelBps
            ? [CreateVote(1001, instrumentSlot, modelBps, market.StateVersion, eventNs, nowTicks, validUntil)]
            : selection is not null
            ? [CreateVote(StrategyExpertId, instrumentSlot, expectedMoveBps, market.StateVersion, eventNs, nowTicks, validUntil)]
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
                nowTicks, route.AssetClass,
                verifiedStrategyFamily ?? selection?.Strategy.Id ?? DefaultStrategyFamily,
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
