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
using QuantDesk.Runtime.Research;
using QuantDesk.Runtime.Risk;
using QuantDesk.Runtime.Scoring;
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
    Func<TradedAssetClass, IReadOnlyList<SignalStrategy>>? tradableStrategies = null,
    ShadowSignalLog? shadow = null,
    TimeSpan shadowHoldingPeriod = default,
    IndicatorRegimeSource? regimes = null,
    IForecastCalibrationSource? calibration = null)
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
        IReadOnlyDictionary<string, int> openByMechanism,
        bool explorationBudgetAvailable,
        out IReadOnlyList<string> unavailable,
        int instrumentSlotForRegime = 0)
    {
        unavailable = [];
        // Session scoping is an asset-class property, not a setting. Crypto has no session to reset
        // on, so a session-scoped VWAP there would accumulate from whenever the history happens to
        // begin; equities have one, and VWAP is defined against it.
        bool sessionScoped = route.AssetClass is not TradedAssetClass.SpotCrypto;

        IndicatorSet? indicators = evidence.HasFullBars
            ? IndicatorSet.Build(
                evidence.Closes, evidence.Highs, evidence.Lows, evidence.Volumes,
                evidence.HasTimestamps ? evidence.Timestamps : null,
                sessionScoped)
            : null;

        // Only strategies not already measured to lose. See SignalStrategies.IsKnownToLose: being
        // unproven is worth paying a little to resolve, being demonstrably unprofitable is not.
        IReadOnlyList<SignalStrategy> available = _tradable(route.AssetClass);

        // Nothing qualifies, and a budget exists to find out why.
        //
        // Falling back rather than widening: the qualified book is always preferred, and the
        // explorable one is consulted only when the qualified one is empty. So a rule that earns
        // its way back immediately displaces the exploration it was funded by, which is the whole
        // point of paying for the evidence.
        bool exploring = available.Count == 0 && explorationBudgetAvailable;
        // Per book, and filtered by what shadow already knows: the budget buys fills, spread
        // and slippage, which shadow cannot see, never a repeat of a verdict it has already
        // reached for free.
        if (exploring)
            available = SignalStrategies.Explorable(route.AssetClass, shadow?.Summarise(route.AssetClass));
        if (indicators is not null)
        {
            unavailable = indicators.Unavailable;

            // Every rule is asked, not only the tradable ones, and what fires is recorded whether
            // or not it is allowed to trade. Otherwise a stood-down rule can never earn its way
            // back: it produces no evidence because it does not trade, and it does not trade
            // because it has no evidence. Shadow is the rung of section 20.4's ladder that closes
            // that loop, and it costs nothing but a dictionary write.
            RecordShadowSignals(SignalStrategies.For(route.AssetClass), indicators, route, evidence);

            // Classify the regime while the bars are in hand. The exit engine needs it on another
            // thread and has no way to build an indicator set of its own.
            regimes?.Observe(
                route.Symbol, indicators, instrumentSlotForRegime,
                clock.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L,
                clock.MonotonicTimestamp,
                sourceStateVersion: 0);

            return rotation.Select(available, indicators, openByMechanism, MechanismCap(available));
        }

        // Closes only, or too little history for the slower indicators. Rather than fall silent,
        // fall back to the families that genuinely need nothing else -- which is what the lane
        // traded before any of this existed. Going quiet here would be the worse failure: a feed
        // that briefly returns short history would stop the lane without any signal that it had.
        // Taken from the tradable book and filtered by the closes-only identities, not the other
        // way round. Selecting the registry's own objects and merely checking their ids against the
        // tradable set meant the two paths could hand back different objects for the same rule --
        // same id, different measured figures, and now a different expected edge, because the
        // expected return published for a candidate is read off the strategy that fired.
        HashSet<string> closesOnlyIds =
        [
            .. SignalStrategies.ClosesOnly(route.AssetClass).Select(item => item.Id),
        ];
        IReadOnlyList<SignalStrategy> closesOnly =
            [.. available.Where(item => closesOnlyIds.Contains(item.Id))];
        return closesOnly.Count == 0
            ? StrategyEvaluation.None
            : rotation.Select(closesOnly, CloseOnlySet(evidence), openByMechanism, MechanismCap(available));
    }

    /// <summary>
    /// Records every rule that fired on this bar, traded or not.
    ///
    /// The identity is the rule, the symbol and the bar's own minute, so two cycles landing on the
    /// same bar produce one signal. Weighting the sample by how often the lane happened to run
    /// rather than by how often the rule fired would make the evidence a fact about the scheduler.
    ///
    /// A rule that throws is skipped here without ceremony: the trading path already reports that
    /// separately, and a shadow log is not the place to learn about it.
    /// </summary>
    /// <summary>
    /// The holding period a shadow signal is scored over when the lane has not supplied one.
    ///
    /// Four hours, matching the crypto lane. A shadow result is only comparable to a real one if
    /// they are measured over the same horizon, so this is a fallback rather than a policy.
    /// </summary>
    private static readonly TimeSpan DefaultShadowHold = TimeSpan.FromHours(4);

    private void RecordShadowSignals(
        IReadOnlyList<SignalStrategy> strategies,
        IndicatorSet indicators,
        OpportunityRoute route,
        DirectionalMarketEvidence evidence)
    {
        if (shadow is null) return;

        int last = indicators.Length - 1;
        if (last < 0) return;

        decimal reference = (evidence.Bid + evidence.Ask) / 2m;
        if (reference <= 0m) return;

        DateTimeOffset firedAt = indicators.HasTimeAxis ? indicators.Timestamps[last] : clock.UtcNow;
        double venueCost = VenueRoundTripCosts.For(route.AssetClass);

        // Collected first, written once. One evaluation asks every rule and several fire together,
        // so recording them one at a time made the cost of a cycle quadratic in the size of the log.
        List<ShadowSignal> fired = [];
        foreach (SignalStrategy strategy in strategies)
        {
            SignalDirection firedNow;
            try
            {
                firedNow = strategy.Fires(indicators, last);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Failed, not abstained. The two look identical from outside -- neither produces a
                // signal -- but one is a market observation and the other is a broken rule, and a
                // rule that throws every time would accumulate no evidence for as long as it ran
                // while appearing to be evaluated. That is the exact failure shadow exists to
                // prevent: a strategy stood down for want of evidence it was never able to produce.
                logger.LogWarning(
                    exception,
                    "Strategy {Strategy} threw while being evaluated in shadow on {Symbol}.",
                    strategy.Id, route.Symbol);
                continue;
            }

            if (firedNow is SignalDirection.None) continue;

            fired.Add(new ShadowSignal(
                SignalId: $"{strategy.Id}|{route.Symbol}|{firedAt:yyyyMMddTHHmm}",
                Symbol: route.Symbol,
                StrategyId: strategy.Id,
                FiredAt: firedAt,
                EntryReferencePrice: reference,
                ResolveAt: firedAt.Add(
                    shadowHoldingPeriod > TimeSpan.Zero ? shadowHoldingPeriod : DefaultShadowHold),
                VenueRoundTripBps: venueCost)
            {
                // The route's own answer rather than one inferred from the symbol later. Both books
                // define rules under the same identifiers, so a summary that cannot tell them apart
                // decides tradability for one book using the other's evidence.
                AssetClass = route.AssetClass,
                Direction = firedNow,
            });
        }

        shadow.TryRecordMany(fired);
    }

    /// <summary>
    /// The lane's word for each way the committee can decline.
    ///
    /// Kept as distinct strings because they call for different responses. A conflict between two
    /// mechanisms is the committee working -- contradiction is evidence of uncertainty, and section
    /// 26.2 refuses to average it into a direction. Missing experts is a plumbing fault. Reading
    /// one as the other sends whoever is on the other end of the status page looking in the wrong
    /// place, which is what happened at this morning's open.
    /// </summary>
    private static string CommitteeRefusal(string reasonCode) => reasonCode switch
    {
        "no_experts" => "NoExpertsVoted",
        "insufficient_valid_evidence" => "InsufficientExpertAvailability",
        "mechanism_conflict" => "MechanismConflict",
        "committee_disagreement" => "CommitteeDisagreement",
        _ => "CommitteeRefused",
    };

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
        return IndicatorSet.Build(
                evidence.Closes, evidence.Closes, evidence.Closes, zeroVolume,
                evidence.HasTimestamps ? evidence.Timestamps : null)
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
        double? allInCostUpperBoundBps = null,
        Usd projectedCorrelatedExposure = default,
        bool explorationBudgetAvailable = false,
        Usd? maximumCorrelatedExposure = null)
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
            StrategyEvaluation evaluation = SelectStrategy(
                evidence, route, openByMechanism, explorationBudgetAvailable,
                out IReadOnlyList<string> unavailable);
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

                // A feature the history cannot support is not a quiet market either. An entirely
                // NaN series makes every rule reading it decline, silently and forever, which is
                // indistinguishable from nothing happening -- and it happened within hours of the
                // time-of-day volume baseline landing, because it needs five prior days and the
                // crypto client fetches twenty-four hours.
                //
                // Reported only when a rule that could otherwise have traded actually needs the
                // missing series. Saying "IndicatorUnavailable" because some series is missing
                // would claim a cause that has not been established -- the market may simply be
                // quiet -- and a reason that is confidently wrong is worse than a vague one.
                string? blocking = unavailable.FirstOrDefault(missing =>
                    _tradable(route.AssetClass).Any(strategy =>
                        strategy.RequiredSeries.Any(series =>
                            missing.StartsWith(series, StringComparison.Ordinal))));
                if (blocking is not null)
                {
                    logger.LogWarning(
                        "Indicator unavailable for {Symbol}: {Reason}.", route.Symbol, blocking);
                    return Reject($"IndicatorUnavailable:{blocking}");
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

            // What the candidate is expected to earn is the rule's own measured edge, not the
            // instrument's expected travel.
            //
            // The viability figure above is an ATR magnitude scaled by the square root of the
            // holding period: how far this instrument typically moves, saying nothing about which
            // way. Publishing it as the expected return claimed the position would capture the
            // whole typical move. On 2026-09-02 that put 170 bps on a candidate whose rule is
            // measured at 1.5 bps net -- about 35 gross -- and it did two kinds of damage at once.
            // The risk governor's net-edge gate compares gross expected P&L against cost, so a
            // hundredfold overstatement turned a discriminating check into a rubber stamp. And the
            // profit target is a fraction of the same number, so it sat five times further away
            // than a round trip costs and could never be reached: every position ran to its timer
            // or its stop, and the target looked wired up the whole time.
            //
            // Gross, because the cost is subtracted again downstream. Subtracting it twice would
            // understate every edge by a full round trip.
            // Signed by the rule's own direction. ResearchMeanGrossBps is a magnitude -- how much
            // this rule has historically earned when it fired -- and the vote built from it is what
            // the committee, the forecast and the compiler all read as "which way". Publishing the
            // magnitude for a Short would have described a bearish rule to everything downstream as
            // a bullish one, which is the sign-reversal hazard wearing a different hat.
            expectedMoveBps = selection.Direction is SignalDirection.Short
                ? -selection.Strategy.ResearchMeanGrossBps
                : selection.Strategy.ResearchMeanGrossBps;

            // Spot crypto cannot be shorted at the venue: there is no borrow, and Alpaca offers no
            // paper crypto derivative. Refused here rather than left to the compiler, which also
            // refuses it, so that an operator reading the status page sees the instrument's reason
            // instead of a bare NoOpportunity.
            if (selection.Direction is SignalDirection.Short &&
                route.AssetClass is TradedAssetClass.SpotCrypto)
            {
                return Reject("ShortNotSupportedOnSpotCrypto");
            }

            // The instrument still has to be able to move enough to pay for the round trip. That is
            // a necessary condition and stays exactly where it was; it is simply no longer mistaken
            // for a forecast.
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

        // Through the clock. Computed from Stopwatch.Frequency it was out by a factor of a
        // hundred whenever nowTicks came from a virtual clock, because the two count in different
        // units on Linux -- so a vote that should have expired in five minutes did not expire at
        // all, and the replay of a session would have taken a branch the session never took.
        long validUntil = nowTicks + clock.MonotonicTicksFor(TimeSpan.FromMinutes(5));
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
            ? [CreateVote(1001, instrumentSlot, route.AssetClass, modelBps, market.StateVersion, eventNs, nowTicks, validUntil)]
            : selection is not null
            ? [CreateVote(StrategyExpertId, instrumentSlot, route.AssetClass, expectedMoveBps, market.StateVersion, eventNs, nowTicks, validUntil, selection.Strategy)]
            :
            [
                CreateVote(MediumTrendExpertId, instrumentSlot, route.AssetClass, ReturnBps(evidence.Closes[^13], evidence.Closes[^1]), market.StateVersion, eventNs, nowTicks, validUntil),
                CreateVote(ShortMomentumExpertId, instrumentSlot, route.AssetClass, ReturnBps(evidence.Closes[^4], evidence.Closes[^1]), market.StateVersion, eventNs + 1, nowTicks, validUntil)
            ];
        CommitteeDecision committeeDecision = committee.Evaluate(
            instrumentSlot, votes, nowTicks, market.StateVersion);
        if (!committeeDecision.Actionable)
        {
            // The committee's own reason, not a single label for all of them.
            //
            // Four distinct outcomes reached here -- no_experts, insufficient_valid_evidence,
            // mechanism_conflict and committee_disagreement -- and every one was reported as
            // "InsufficientExpertAvailability", which names only the first two and actively
            // misdescribes the others. With the equity book stood down and the fallback votes
            // being a thirteen-bar and a four-bar return, the usual outcome is the two disagreeing
            // in sign: a mechanism conflict, reported for sixteen minutes after the opening bell as
            // an availability problem that did not exist.
            //
            // This file already carries the scar: the comment above records a mean-reversion
            // strategy being refused under this same label "with a reason that pointed at the wrong
            // thing". Collapsing them again is how that recurs.
            return new(
                false,
                CommitteeRefusal(committeeDecision.ReasonCode),
                null, null, null, committeeDecision, market);
        }

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
        ActionabilityAssessment actionable =
            actionability.Evaluate(candidate, estimate, market, explorationBudgetAvailable);
        if (!actionable.Actionable)
            return new(false, actionable.Reason.ToString(), candidate, estimate, null, committeeDecision, market);

        RiskDecision risk = riskGovernor.Evaluate(
            candidate, estimate, market, portfolio, brokerHealthy, portfolioReconciled, nowTicks,
            projectedCorrelatedExposure,
            explorationBudgetAvailable,
            // This lane's cap, scaled to this lane's position size. The governor is shared and its
            // own limits were built from whichever lane was read first.
            maximumCorrelatedExposure);
        return risk.Approved
            ? new(true, "Approved", candidate, estimate, risk, committeeDecision, market)
            : new(false, risk.Reason.ToString(), candidate, estimate, risk, committeeDecision, market);
    }

    /// <summary>
    /// A vote weighted by what this expert has actually been measured to do on this book.
    ///
    /// Both numbers here were literals: a calibration of 0.75 and a weight of 0.5, handed to the
    /// committee on every vote by every expert for every instrument. The committee then averaged
    /// those weights and compared the result against an agreement floor -- so the floor was being
    /// tested against a constant, and the whole apparatus of measured skill, which had been
    /// scoring forecasts against realised outcomes the entire time, reached no decision at all.
    /// The volatility and regime experts read their own measured calibration; the directional votes
    /// that actually decide trades did not.
    ///
    /// Weight is the calibration rather than a separate quantity. Inventing a second dial would
    /// mean two numbers to justify where the evidence supports one, and the committee already reads
    /// calibration for its agreement score -- so a vote that is trusted less now counts less in
    /// both places, which is what trusting it less means.
    ///
    /// An expert with no record on this book falls to <see cref="ForecastCalibration.Unmeasured"/>,
    /// which sits exactly at the committee's floor: neither trusted nor refused while nothing is
    /// known, and refused the moment a measurement says it should be. Crucially it is per book --
    /// a record earned on crypto no longer speaks for equities, which is the same rule that stopped
    /// a BTC-fitted variance model forecasting four equity ETFs.
    /// </summary>
    private ExpertVote CreateVote(
        int expertId, int slot, TradedAssetClass assetClass, double expectedReturnBps,
        long stateVersion, long eventNs, long nowTicks, long validUntil,
        SignalStrategy? strategy = null)
    {
        double measured = MeasuredWeight(expertId, assetClass, strategy);

        return new ExpertVote(
            expertId,
            CreateForecast(expertId, slot, expectedReturnBps, measured, stateVersion,
                eventNs, nowTicks, validUntil),
            measured);
    }

    /// <summary>
    /// What this voter's own record says its opinion is worth on this book.
    ///
    /// Two kinds of voter, two kinds of record, and neither is a constant. A rule's evidence is its
    /// measured net edge against the venue's real toll, which the strategy registry has carried all
    /// along; a model's is the forecast scorer's, which needs twelve independent episodes before it
    /// says anything. Where neither exists the vote falls to the unmeasured default -- which the
    /// edge-confidence function returns anyway for a zero edge, so the two agree by construction
    /// rather than by coincidence.
    ///
    /// Per book in both cases. A record earned on continuously-traded crypto says nothing about an
    /// equity ETF with an opening auction and a close, and the venue tolls differ by a factor of
    /// seven.
    /// </summary>
    private double MeasuredWeight(
        int expertId, TradedAssetClass assetClass, SignalStrategy? strategy)
    {
        if (strategy is not null)
        {
            // Net of what this venue actually charges, not of what the research scan assumed. The
            // two differ by about 26 bps on crypto, and weighting on the assumption would trust a
            // rule exactly where it loses money.
            double venueCost = VenueRoundTripCosts.For(assetClass);
            double meanNet = strategy.ResearchMeanGrossBps - venueCost;
            double lowerNet = strategy.ResearchLowerBoundBps
                + strategy.ResearchCostAssumptionBps - venueCost;

            // Live shadow overrules the backtest in both directions, once it has enough signals to
            // mean anything -- the same precedence the tradable filter applies.
            if (shadow?.Summarise(assetClass) is { } summaries &&
                summaries.TryGetValue(strategy.Id, out ShadowSummary live) &&
                live.Signals >= MinimumShadowSignalsForWeight)
            {
                meanNet = live.MeanNetBps;
                lowerNet = live.LowerBoundBps;
            }

            return MeasuredEdgeConfidence.From(meanNet, lowerNet);
        }

        return calibration?.For(expertId, ForecastType.DirectionalReturn, assetClass)
            ?? ForecastCalibration.Unmeasured;
    }

    /// <summary>Signals before live shadow evidence outranks the research record, matching the rule book.</summary>
    private const int MinimumShadowSignalsForWeight = 12;

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
