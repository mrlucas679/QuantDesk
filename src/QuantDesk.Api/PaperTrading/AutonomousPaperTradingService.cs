using QuantDesk.Domain.Risk;
using System.Diagnostics;
using QuantDesk.Alpaca.Capabilities;
using QuantDesk.Domain.Capabilities;
using QuantDesk.Alpaca.Mapping;
using QuantDesk.Alpaca.MarketData;
using QuantDesk.Domain.Execution;
using QuantDesk.Domain.Contracts;
using QuantDesk.Domain.Market;
using QuantDesk.Domain.Numerics;
using QuantDesk.Domain.Portfolio;
using QuantDesk.Domain.Runtime;
using QuantDesk.Domain.Strategies;
using QuantDesk.Domain.Trading;
using QuantDesk.Runtime.Costs;
using QuantDesk.Runtime.Execution;
using QuantDesk.Runtime.Indicators;
using QuantDesk.Runtime.Modes;
using QuantDesk.Runtime.State;
using QuantDesk.Runtime.Portfolio;
using QuantDesk.Runtime.Positions;
using QuantDesk.Runtime.Reconciliation;
using QuantDesk.Runtime.Persistence;
using QuantDesk.Runtime.Research;
using QuantDesk.Runtime.Telemetry;
using QuantDesk.Runtime.Risk;
using QuantDesk.Runtime.Reservations;
using QuantDesk.Runtime.Time;

namespace QuantDesk.Api.PaperTrading;

/// <summary>Supervises autonomous paper opportunities through the deterministic runtime.</summary>
public sealed class AutonomousPaperTradingService(
    IBrokerExecutionGateway broker,
    IInstrumentSymbolResolver symbols,
    IRealisedCostSource realisedCosts,
    SpotExecutionStore spotStore,
    MarketStateStore marketState,
    StrategyRotation rotation,
    AlpacaMarketClock marketClock,
    IMarketEvidenceProvider evidenceProvider,
    BrokerExposureAttributor attributor,
    OpportunityRouter router,
    OptionExecutionCoordinator optionExecution,
    SpotExecutionLifecycle spotExecution,
    IAlpacaCapabilityProbe capabilityProbe,
    AutonomousDecisionPipeline pipeline,
    ResearchArtifactState researchArtifacts,
    AutonomousPaperTradingOptions options,
    RuntimeModeState runtimeMode,
    AutonomousTradingState state,
    IRuntimeClock clock,
    ReturnSeriesCache returnSeries,
    ShadowSignalLog shadow,
    IHeldPositionMarker heldMarker,
    AlpacaCryptoOrderBookClient? orderBooks,
    LatencyRecorder? latency,
    ILogger<AutonomousPaperTradingService> logger) : BackgroundService
{
    private static readonly TimeSpan PositionMonitorInterval = TimeSpan.FromSeconds(5);

    /// <summary>Expiry window for an options expression of a short-horizon directional view.</summary>
    private const int MinimumOptionDaysToExpiry = 7;
    private const int MaximumOptionDaysToExpiry = 60;

    /// <summary>Strikes considered around spot, bounded so one quote request covers the band.</summary>
    private const decimal OptionStrikeBandFraction = 0.05m;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) { state.Update("disabled", options.Symbol); return; }

        // Rebuild how many live trades each strategy already has, from the durable records.
        //
        // The rotation balances by trade count, and that count lived only in memory. Every restart
        // reset it, so a day containing several deploys is several independent windows each
        // starting from zero -- and in every one of them the strategy that fires most often is
        // picked first. The sample tilts toward it while the code still looks like it is
        // balancing, which is the kind of bias that leaves no trace of itself in the result.
        //
        // Only records that actually opened a position count. RecordTrade is called on execution
        // rather than on selection for exactly this reason, and restoring from every record undid
        // that: six orders the venue rejected out of hours were credited as trades, so ema-cross
        // carried three trades having never once held anything, and the rotation began pushing the
        // strategy with the least real evidence to the back of the queue.
        rotation.RestoreFrom(
            spotStore.ListAll()
                .Where(record => record.EntryFilledQuantity > 0m)
                .Select(record => record.StrategyId),
            SignalStrategies.ForCrypto.Concat(SignalStrategies.ForEquity).ToArray());
        try
        {
            await WaitUntilReadyAsync(stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await EvaluateOneCycleAsync(stoppingToken);
                await Task.Delay(options.CycleInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            state.Update("stopped", options.Symbol);
        }
        catch (Exception exception)
        {
            state.Update("failed", options.Symbol, reason: "AUTONOMOUS_PIPELINE_FAILED");
            runtimeMode.Transition(SystemMode.Degraded, "autonomous_pipeline_failed");
            logger.LogError(exception, "Autonomous paper runtime failed closed.");
        }
    }

    /// <summary>
    /// Runs one evaluation, and treats a failed one as an abstention rather than the end of the lane.
    ///
    /// The try used to wrap the whole loop, so a single failed cycle exited it permanently and left the
    /// autonomous trader dead until the process restarted. The condition that exposed it is the most
    /// ordinary one there is: outside regular hours SPY has no two-sided quote, the evidence provider
    /// throws, and the lane stopped for good — for roughly nineteen hours of every day, and every
    /// weekend.
    ///
    /// An unreachable venue or an unquotable market is a reason to skip this cycle and look again in a
    /// minute. It is not a reason to stop trading forever, and not to do so under a state of "failed"
    /// that reads like a defect in the strategy.
    /// </summary>
    private async Task EvaluateOneCycleAsync(CancellationToken stoppingToken)
    {
        // Each symbol is evaluated independently and fails independently. One instrument's feed
        // outage, unroutable ticker, or closed session must not stop the lane looking at the
        // others -- with a single symbol that distinction did not exist, and inheriting it would
        // mean the least reliable instrument silently governed the whole lane.
        foreach (string symbol in options.Symbols)
        {
            if (stoppingToken.IsCancellationRequested) return;
            await EvaluateSymbolAsync(symbol, stoppingToken);
        }
    }

    private async Task EvaluateSymbolAsync(string symbol, CancellationToken stoppingToken)
    {
        try
        {
            await EvaluateOpportunityAsync(symbol, stoppingToken);
        }
        catch (Exception exception) when (HostedServiceFaults.IsFault(exception, stoppingToken))
        {
            // A closed session is not a fault, and reporting it as one hides real ones. Both used to
            // arrive here identically -- a quote with no two-sided spread -- so an equity lane
            // logged the same warning and stack trace every cycle for roughly sixteen hours a day,
            // which is how an operator learns to ignore the line that matters when the feed does
            // break. The venue is asked rather than the failure's shape inferred from.
            if (await IsSessionClosedAsync(symbol, stoppingToken))
            {
                state.UpdateSymbol(symbol, "abstained", symbol, reason: "MarketClosed");
                logger.LogDebug("Autonomous cycle abstained for {Symbol}: the session is closed.", symbol);
                return;
            }

            state.UpdateSymbol(symbol, "abstained", symbol, reason: "EvidenceUnavailable");
            logger.LogWarning(
                exception,
                "Autonomous cycle abstained for {Symbol}: market evidence was unavailable. The lane continues.",
                symbol);
        }
    }

    internal async Task EvaluateOpportunityAsync(string symbol, CancellationToken cancellationToken)
    {
        bool experimental = options.Mode == AutonomousTradingMode.ExperimentalPaper;
        if (runtimeMode.Snapshot().Mode != SystemMode.Ready && !experimental)
        {
            state.UpdateSymbol(symbol, "entry_halted", symbol, reason: "RuntimeNotReady");
            return;
        }
        // Route first: an unsupported symbol must never inherit another asset class's cost model,
        // order policy, or permission check. Routing also precedes slot resolution, because an
        // unroutable symbol is a configuration abstention, not a runtime fault — throwing here
        // would trip the catch-all and degrade the whole runtime over a bad setting.
        if (!router.TryRoute(symbol, out OpportunityRoute? route, out string routeReason) ||
            route is null)
        {
            state.UpdateSymbol(symbol, "abstained", symbol, reason: routeReason);
            return;
        }

        // The session, before anything is evaluated.
        //
        // This used to be asked only in the catch handler, so it caught a symbol whose quote had
        // failed and missed one whose data arrived fine. Outside market hours QQQ and DIA returned
        // perfectly good bars, passed every gate, and sent orders the venue rejected outright --
        // "ioc orders are only accepted during market hours" -- once per symbol per cycle. Nothing
        // was lost because nothing filled, but the lane was hammering the broker with orders that
        // could not be accepted, and reporting the venue's refusal as its own decision.
        if (await IsSessionClosedAsync(symbol, cancellationToken))
        {
            state.UpdateSymbol(symbol, "abstained", symbol, reason: "MarketClosed");
            return;
        }

        if (!symbols.TryResolveBySymbol(symbol, out int slot))
        {
            state.UpdateSymbol(symbol, "abstained", symbol, reason: "SymbolNotMappedToInstrumentSlot");
            logger.LogWarning(
                "Autonomous symbol {Symbol} routed to {AssetClass} but is not mapped to an instrument slot.",
                symbol, route.AssetClass);
            return;
        }

        CapabilityReport probed = await capabilityProbe.ProbeAsync(cancellationToken);
        var capabilities = new AccountCapabilities(
            probed.PaperEnvironment, probed.EquityTrading, probed.CryptoTrading,
            probed.OptionsTrading, probed.OptionsTradingLevel);
        if (!route.IsPermittedBy(capabilities))
        {
            state.UpdateSymbol(symbol, "abstained", symbol, reason: "AssetClassNotPermitted");
            logger.LogWarning(
                "Autonomous route {AssetClass} is not permitted by the live account.", route.AssetClass);
            return;
        }
        ResearchArtifactSnapshot research = researchArtifacts.Snapshot();
        ForecastSnapshotContract? forecast = research.Forecast;
        if (!experimental && (!research.Ready || forecast is null))
        {
            state.UpdateSymbol(symbol, "abstained", symbol, reason: "VerifiedForecastUnavailable");
            return;
        }
        if (!experimental && (!string.Equals(forecast!.Instrument, symbol, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(forecast.ForecastFamily, "directional_return_bps", StringComparison.OrdinalIgnoreCase) ||
            research.StrategyDefinition is null ||
            !string.Equals(research.StrategyDefinition.Symbol, symbol, StringComparison.OrdinalIgnoreCase))
            )
        {
            state.UpdateSymbol(symbol, "abstained", symbol, reason: "VerifiedForecastIncompatible");
            return;
        }

        BrokerAccountSnapshot account = await RequireHealthyAccountAsync(cancellationToken);
        IReadOnlyList<BrokerOrderSnapshot> openOrders = await broker.ListOpenOrdersAsync(cancellationToken);
        IReadOnlyList<BrokerPositionSnapshot> brokerPositions = await broker.ListPositionsAsync(cancellationToken);
        // Exposure this system created is not a reason to halt; exposure nobody can account for is.
        // The gate used to refuse both, so one lane holding an unrelated instrument stopped every other
        // lane, and a genuinely foreign position was indistinguishable from our own.
        BrokerExposureAttribution attribution = attributor.Attribute(openOrders, brokerPositions);
        if (attribution.HasUnattributedExposure)
        {
            runtimeMode.Transition(SystemMode.EntryHalted, "broker_state_requires_reconciliation");
            state.UpdateSymbol(symbol, "entry_halted", symbol, reason: "PortfolioUnreconciled");
            logger.LogWarning(
                "Entry halted: {Attribution}. Nothing will trade until this is explained or closed.",
                attribution.Describe());
            return;
        }

        // Attributed exposure in the instrument we are about to trade is still disqualifying: a second
        // position in the same symbol would trade over the lane that already holds it.
        //
        // This lock is what limits the system to one strategy per symbol, and
        // PortfolioIntentAggregator exists to replace it — netting several strategies' intents into
        // one target instead of refusing the second outright. It is deliberately not wired here yet,
        // and the precondition is worth stating so this lock is not removed on the strength of the
        // aggregator's existence.
        //
        // Execution below opens a fixed OrderNotional every time; it does not move an existing
        // position toward a target. Netting is only safe against an executor that sends the
        // *difference* between held and wanted, which is why the aggregator exposes RequiredDelta.
        // Remove this lock and the fixed-size path would add a second full position rather than
        // adjusting the first — doubling exposure, which is the exact failure the lock prevents.
        //
        // Order of work: make spot execution delta-based, then net, then drop this.
        if (attribution.IsClaimed(route.Symbol))
        {
            // Refresh the market state before standing down.
            //
            // Nothing else does it for a held symbol. The quote is applied inside the decision
            // pipeline, and the pipeline is exactly what this early return skips -- so a position's
            // price stopped updating the moment it opened, and stayed frozen at its entry for the
            // whole holding period.
            //
            // Two things read that price, and both were quietly wrong. The adverse-loss stop
            // compares the current mid against the entry to decide whether the position has lost
            // more than it was authorised to: against a frozen mid the unrealised loss computes as
            // roughly zero, so the stop could never fire however far the position ran. And the
            // exit reference price is taken from the same state when the trip reconciles, so the
            // implementation shortfall this system measures its costs with would have been
            // calculated against an entry-era price.
            //
            // A failure here is not fatal: the stop declines on a missing quote by design, which is
            // the same position it was in before, so the hold simply falls back to its timer.
            await RefreshMarketStateAsync(route, slot, cancellationToken);

            // Reported as holding, not as an abstention. The *decision* is to abstain from a new
            // entry, but the instrument's *state* is that it holds a position, and conflating the
            // two made the lane read as entirely flat while it was holding: the entry cycle set
            // "holding" once, then every subsequent cycle overwrote it with an abstention. An
            // operator watching the status endpoint would have seen no open position anywhere.
            state.UpdateSymbol(
                symbol, "holding", symbol,
                filledQuantity: HeldQuantity(route.Symbol),
                reason: "SymbolAlreadyHeld");
            return;
        }

        PortfolioSnapshot initial = EmptyPortfolio(account);
        // Timed separately from the decision that follows it. A cycle slow because the venue is
        // slow and one slow because this system is slow need different responses, and a single
        // end-to-end number cannot tell them apart.
        long fetchStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        DirectionalMarketEvidence evidence = await evidenceProvider.GetEvidenceAsync(route, cancellationToken);
        latency?.Record(LatencyStage.MarketDataFetch, fetchStarted);

        // Kept for the correlation measurement below. Costs no extra market-data call: these are
        // the bars the decision is about to be made from anyway.
        returnSeries.Record(route.Symbol, evidence.Closes);

        // Read the resting book before deciding, not only while holding.
        //
        // The refresh path that first carried this runs for symbols the account already holds, so
        // depth was being read for every position except the one about to be opened -- exactly
        // backwards for a signal whose whole purpose is to say something about entering. One extra
        // call per instrument per cycle, on the same cadence as the quote it accompanies.
        long bookEventNs = clock.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L;
        long bookStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        await RefreshOrderBookAsync(route, slot, bookEventNs, cancellationToken);
        latency?.Record(LatencyStage.OrderBookFetch, bookStarted);

        // Close out any shadow signal whose holding period has ended, using the quote this cycle
        // already fetched. Done here rather than on a timer of its own so that a signal is scored
        // against a price the lane genuinely saw, not one read at an unrelated moment.
        shadow.Resolve(clock.UtcNow, symbol =>
            BrokerSymbol.Matches(symbol, route.Symbol) ? (evidence.Bid + evidence.Ask) / 2m : null);

        // What the book would actually be exposed to if this position opened, measured here because
        // this is where the return history is, and handed to the risk governor because that is
        // where risk is decided. It used to be enforced here, as a lane-local gate -- which left
        // every other path to a position, the diagnostic and options lanes included, free of it.
        CorrelationBreadthDecision breadth = CorrelationBreadthGate.Evaluate(
            route.Symbol,
            HeldSymbols(),
            returnSeries.Snapshot(),
            options.OrderNotional,
            RiskLimitOptions.MaximumCorrelatedExposure(options.OrderNotional));
        // Is there room in the exploration budget?
        //
        // Counted against positions actually open rather than against a running total, so the
        // budget is a bound on concurrent exposure and not a daily quota that silently exhausts.
        // Zero allowance -- the default -- makes this false always, and the desk stands down on the
        // evidence rather than paying to ignore it.
        bool explorationBudgetAvailable =
            options.ExplorationEnabled && HeldSymbols().Count < options.ExplorationAllowance;

        // The gap the entry fence guards. Whether a 30 bps adverse-move bound protects anything
        // depends on how long this actually takes, and until now nothing measured it.
        long decisionStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        AutonomousPipelineDecision decision = pipeline.Evaluate(
            slot, route, evidence, initial, true, true, capabilities,
            // What each mechanism already holds, so one cannot monopolise the universe and leave
            // the others unsampled when their conditions finally appear.
            OpenPositionsByMechanism(),
            experimental ? null : (double)forecast!.PointForecast,
            experimental ? null : research.StrategyFamily,
            experimental ? null : research.StrategyDefinition,
            // The forecast's own error bar and the measured cost bound. Gross enters the comparison
            // at its lower bound and cost at its upper, so neither estimate's error argues for the
            // trade. Experimental mode has no verified forecast and no bound to apply.
            experimental ? null : forecast!.Uncertainty,
            experimental ? null : MeasuredCostUpperBoundBps(),
            new Usd(breadth.CorrelatedExposure),
            explorationBudgetAvailable);
        latency?.Record(LatencyStage.Decision, decisionStarted);

        if (!decision.Approved || decision.Candidate is not TradeCandidate candidate ||
            decision.Risk is not { Approved: true } risk)
        {
            state.UpdateSymbol(symbol, "abstained", symbol, reason: decision.Reason,
                grossEdgeBps: decision.Committee is { } committee
                    ? (decimal)committee.ExpectedReturnBps
                    : null);
            logger.LogInformation("Autonomous decision for {Symbol}: {Reason}.", symbol, decision.Reason);
            return;
        }

        // Refuse an order too small to pay its own costs. Fixed venue charges do not shrink with
        // order size, so below a certain notional the broker takes more than the edge can win and
        // the trade loses whichever way the market moves.
        decimal grossEdgeBps = decision.Committee is { } edge
            ? (decimal)Math.Abs(edge.ExpectedReturnBps)
            : 0m;
        if (!route.Costs.IsEconomicallyViable(
                options.OrderNotional, grossEdgeBps, spreadBps: 0m, out string viability,
                explorationBudgetAvailable))
        {
            state.UpdateSymbol(symbol, "abstained", symbol, reason: viability);
            logger.LogInformation(
                "Autonomous opportunity for {Symbol} refused as uneconomic at {Notional}: {Reason}. " +
                "Minimum viable notional is {Minimum}.",
                symbol, options.OrderNotional, viability,
                route.Costs.MinimumViableNotionalUsd(grossEdgeBps));
            return;
        }

        // Bound here, before either branch, because both open positions and both must be able to
        // name what licensed them. In experimental mode there is no verified artifact and the
        // binding is null -- an honest record that no research stands behind the position, rather
        // than a missing field.
        PositionOwnership? ownership = experimental
            ? null
            : ResearchPositionOwnership.Bind(research, clock.UtcNow);

        // A defined-risk vertical expresses the same directional view the pipeline just approved,
        // using options instead of the underlying. The view is formed on the underlying above;
        // only the instrument differs, so the branch happens here and not earlier.
        if (options.Expression == OpportunityExpression.DefinedRiskVertical)
        {
            await ExecuteOptionOpportunityAsync(
                symbol, capabilities, candidate, decision, ownership, cancellationToken);
            return;
        }

        long executionStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        await ExecuteSpotOpportunityAsync(
            symbol, candidate, decision, evidence, slot, ownership, cancellationToken);
        latency?.Record(LatencyStage.BrokerSubmit, executionStarted);
    }

    /// <summary>
    /// Hands an approved spot view to the durable lifecycle.
    ///
    /// This replaced an in-memory path that reserved, submitted, and then blocked the evaluation
    /// cycle waiting for a fill before managing the exit inline. Two things were wrong with that.
    /// The reservation lived only in process memory, so a restart between reserving and filling
    /// forgot the order existed. And because the cycle owned the position for its whole life, a
    /// crash mid-hold left it with nobody to exit it.
    ///
    /// Now the reservation is persisted before any POST and the recovery worker advances fills,
    /// the durable hold, the managed exit, and reconciliation. The cycle returns immediately.
    /// </summary>
    private async Task ExecuteSpotOpportunityAsync(
        string symbol,
        TradeCandidate candidate,
        AutonomousPipelineDecision decision,
        DirectionalMarketEvidence evidence,
        int slot,
        PositionOwnership? ownership,
        CancellationToken cancellationToken)
    {
        decimal quantity = decimal.Round(
            options.OrderNotional / evidence.Ask, 8, MidpointRounding.ToZero);
        if (quantity <= 0)
        {
            // Rounding to zero means the notional cannot buy a tradable unit at this price.
            state.UpdateSymbol(symbol, "abstained", symbol, reason: "QuantityRoundedToZero");
            return;
        }

        string executionId = DeterministicClientOrderId.Create(
            "autospot", OpportunityIdentity(symbol, candidate, slot), "execution");
        decimal definedMaximumLoss = decision.Risk is { } risk && risk.RequiredRiskReservation.Value > 0
            ? risk.RequiredRiskReservation.Value
            : options.OrderNotional;

        // The decision price and the opening equity, captured before anything reaches the broker.
        // Without them this round trip can never say what it cost: implementation shortfall needs a
        // decision price to measure against, and only account equity sees the venue's separate USD
        // cash charge. Every earlier round trip is unmeasurable for exactly this reason.
        decimal referencePrice = (evidence.Bid + evidence.Ask) / 2m;
        BrokerAccountSnapshot? account = await broker.GetAccountAsync(cancellationToken);

        // What everything else in the account is worth right now, marked at the same instant the
        // opening equity is read. Without it a round trip that shares the account with any other
        // position has no equity delta of its own, and the cost dataset can only ever fill from
        // trading one position at a time -- which this lane does not do.
        IReadOnlyList<PositionMark> marksBefore = MarkOpenPositions(
            await broker.ListPositionsAsync(cancellationToken));

        // The gain at which this position has earned what its own thesis predicted.
        //
        // Taken from the candidate rather than configured, because the number that was chosen in
        // advance for the downside is the defined maximum loss and the number chosen in advance for
        // the upside is this. Without it the exit engine was asymmetric in the wrong direction: a
        // cap on being wrong, and nothing at all on being right, so a correct forecast paid only
        // whatever the market happened to be doing when the timer expired.
        decimal profitTarget = Math.Max(candidate.GrossExpectedPnl.Value, 0m);

        if (!spotExecution.TryReserve(
                executionId, candidate.StrategyId, symbol, slot, quantity,
                definedMaximumLoss, candidate.ManagementPlan.MaximumHoldingPeriod,
                ownership: ownership,
                entryReferencePrice: referencePrice,
                accountEquityBefore: account?.Equity,
                profitTarget: profitTarget,
                positionMarksBefore: marksBefore,
                admittedAsExploration:
                    decision.Risk?.Reason == RiskReason.ApprovedAsExploration,
                // The book as it stood when the decision was made. It cannot be recovered later:
                // a spread read at reconciliation describes a different market.
                decisionRelativeSpread: evidence.Ask > 0m && evidence.Bid > 0m
                    ? (double)((evidence.Ask - evidence.Bid) / ((evidence.Ask + evidence.Bid) / 2m))
                    : null))
        {
            state.UpdateSymbol(symbol, "abstained", symbol, reason: "ReservationRejected");
            return;
        }

        state.UpdateSymbol(symbol, "submitting_entry", symbol, filledQuantity: quantity, reason: "Approved");
        SpotExecutionRecord record = await spotExecution.AdvanceAsync(executionId, cancellationToken);

        if (record.State is SpotExecutionState.Failed)
        {
            state.UpdateSymbol(symbol, "abstained", symbol, reason: record.FailureReason ?? "EntryFailed");
            logger.LogWarning(
                "Autonomous spot entry {ExecutionId} failed: {Reason}.",
                executionId, record.FailureReason);
            return;
        }

        state.UpdateSymbol(symbol, "holding", symbol, record.EntryBrokerOrderId,
            filledQuantity: record.EntryFilledQuantity, reason: record.State.ToString());
        logger.LogInformation(
            "Autonomous spot entry {ClientOrderId} submitted for {Symbol} at quantity {Quantity}; " +
            "the recovery worker owns fills, the hold, and the exit.",
            record.EntryClientOrderId, symbol, quantity);
    }

    /// <summary>
    /// Routes an approved directional view into the durable multi-leg options lifecycle.
    ///
    /// Risk, capital, and reconciliation checks have already run above; the coordinator adds the
    /// options-specific ones — permission for the asset class, an admissible spread, and a debit
    /// that stays inside the risk budget — and commits the reservation durably before any POST.
    /// </summary>
    private async Task ExecuteOptionOpportunityAsync(
        string symbol,
        AccountCapabilities capabilities,
        TradeCandidate candidate,
        AutonomousPipelineDecision decision,
        PositionOwnership? ownership,
        CancellationToken cancellationToken)
    {
        double expectedReturnBps = decision.Committee?.ExpectedReturnBps ?? 0d;
        decimal underlyingPrice = decision.Market is { } market && market.Mid > 0
            ? (decimal)market.Mid
            : 0m;
        string executionId = DeterministicClientOrderId.Create(
            "autoopt", OpportunityIdentity(symbol, candidate, candidate.InstrumentSlot), "execution");

        state.UpdateSymbol(symbol, "submitting_entry", symbol, reason: "OptionSpreadAdmitted");
        OptionExecutionOutcome outcome = await optionExecution.ExecuteAsync(
            symbol,
            capabilities,
            executionId,
            underlyingPrice,
            expectedReturnBps,
            options.OrderNotional,
            candidate.ManagementPlan,
            clock.UtcNow,
            MinimumOptionDaysToExpiry,
            MaximumOptionDaysToExpiry,
            ownership,
            OptionStrikeBandFraction,
            cancellationToken);

        if (!outcome.Submitted)
        {
            state.UpdateSymbol(symbol, "abstained", symbol, reason: outcome.Reason);
            logger.LogInformation(
                "Autonomous option opportunity for {Symbol} was not submitted: {Reason}.",
                symbol, outcome.Reason);
            return;
        }

        // The multi-leg recovery worker owns the record from here: fills, the durable hold, the
        // managed exit, and final reconciliation all advance without this cycle holding state.
        state.UpdateSymbol(symbol, "holding", symbol, reason: outcome.State?.ToString());
        logger.LogInformation(
            "Autonomous option entry {ClientOrderId} submitted for {Symbol}; " +
            "defined maximum loss {Loss}, net debit {Debit}. Lifecycle owns the exit.",
            outcome.EntryClientOrderId, symbol,
            outcome.DefinedMaximumLoss, outcome.NetDebitPerSpread);
    }

    /// <summary>
    /// The reproducible identity of one opportunity. Every component is already persisted with the
    /// candidate, so the same opportunity yields the same client-order IDs on a later pass.
    /// </summary>
    private static string OpportunityIdentity(string symbol, TradeCandidate candidate, int slot) =>
        string.Join(
            '|',
            symbol.Trim().ToUpperInvariant(),
            slot.ToString(System.Globalization.CultureInfo.InvariantCulture),
            candidate.StrategyId,
            candidate.CandidateId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            candidate.SourceStateVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static ExecutionIntent CreateReservedIntent(
        TradeCandidate candidate, PortfolioReservation reservation, string clientOrderId)
    {
        var intent = new ExecutionIntent(candidate.CandidateId, candidate.CandidateId, candidate.StrategyId);
        intent.TransitionTo(ExecutionIntentState.Approved);
        intent.AttachApproval(clientOrderId, reservation.ReservationId, reservation.ReservationId);
        intent.TransitionTo(ExecutionIntentState.Queued);
        return intent;
    }

    private ExecutionCommand CreateCommand(
        TradeCandidate candidate, long reservationId, string clientOrderId, decimal quantity,
        OrderSide side, PositionIntent positionIntent, ExecutionPriority priority)
    {
        long now = clock.MonotonicTimestamp;
        long executionDeadline = priority is ExecutionPriority.ExploitationEntry or ExecutionPriority.ExplorationEntry
            ? Math.Min(candidate.ValidUntilMonotonicTicks, now + Stopwatch.Frequency * 30)
            : now + Stopwatch.Frequency * 30;
        return new ExecutionCommand(now, priority, reservationId, reservationId, clientOrderId,
            candidate.InstrumentSlot, side, positionIntent, ExecutionOrderType.Market,
            ExecutionTimeInForce.Gtc, quantity, null, now,
            executionDeadline, candidate.StrategyId);
    }

    private static void ApplyFill(
        ExecutionIntent intent, ExecutionWorker execution, TradeUpdateProcessor updates,
        BrokerOrderSnapshot fill, int slot, OrderSide side)
    {
        if (fill.AverageFillPrice is not decimal price || price <= 0)
            throw new InvalidOperationException("Filled broker order is missing its average fill price.");
        var update = new BrokerTradeUpdate(BrokerTradeUpdateKind.Fill, fill.ClientOrderId,
            fill.BrokerOrderId, fill.FilledQuantity, price, null,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L);
        execution.ApplyTradeUpdate(intent, update);
        if (!updates.ApplyFill(update, slot, side))
            throw new InvalidOperationException("Broker fill could not be applied to the Portfolio Ledger.");
    }

    private async Task<BrokerAccountSnapshot> RequireHealthyAccountAsync(CancellationToken cancellationToken)
    {
        BrokerAccountSnapshot? account = await broker.GetAccountAsync(cancellationToken);
        if (account is null || account.TradingBlocked || account.AccountBlocked ||
            !string.Equals(account.Status, "ACTIVE", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Paper account is unavailable for autonomous execution.");
        return account;
    }

    /// <summary>
    /// What a round trip of this size has actually cost, at its upper confidence bound.
    ///
    /// Null when nothing has been measured at this size. Passed through as null rather than
    /// replaced by a modelled figure, because an unmeasured cost is not a licence to assume one --
    /// the gate falls back to the older comparison instead of inventing evidence.
    /// </summary>
    /// <summary>
    /// Whether the instrument's session is closed right now.
    ///
    /// Only asked for instruments that have a session: crypto trades continuously, so a failure
    /// there is always worth a warning. A clock call that itself fails answers "not closed", which
    /// keeps the original evidence failure visible rather than letting a second outage disguise the
    /// first as a quiet weekend.
    /// </summary>
    /// <summary>
    /// What the durable store says is open in this instrument.
    ///
    /// Read from the record rather than remembered in the state object, because the state object is
    /// rebuilt every cycle and the record is the thing that survives a restart.
    /// </summary>
    /// <summary>
    /// Applies the current quote for a held instrument to the market state.
    ///
    /// Held positions are otherwise never re-quoted, because the evaluation that applies quotes is
    /// the one an open position skips. Everything that watches a live position -- the loss stop,
    /// and the exit price the cost measurement is computed from -- reads this state.
    /// </summary>
    private async Task RefreshMarketStateAsync(
        OpportunityRoute route, int slot, CancellationToken cancellationToken)
    {
        try
        {
            DirectionalMarketEvidence quote =
                await evidenceProvider.GetEvidenceAsync(route, cancellationToken);
            if (quote.Bid <= 0m || quote.Ask < quote.Bid) return;

            long eventNs = clock.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L;
            marketState.Apply(new QuoteEvent(
                eventNs, slot, (double)quote.Bid, (double)quote.Ask,
                1, 1, eventNs, clock.MonotonicTimestamp, eventNs));

            await RefreshOrderBookAsync(route, slot, eventNs, cancellationToken);
        }
        catch (Exception exception) when (HostedServiceFaults.IsFault(exception, cancellationToken))
        {
            // The stop declines on a missing quote by design, so a failed refresh leaves the hold
            // bounded by its timer -- which is where it was before this existed.
            logger.LogDebug(exception, "Could not refresh the quote for held {Symbol}.", route.Symbol);
        }
    }

    /// <summary>
    /// Reads the resting book and applies it, so depth stops being a field nothing populates.
    ///
    /// InstrumentSnapshot has carried an OrderBookImbalance since the state engine was written, the
    /// store computes it, and the validator checks it -- and no client ever fetched a book, so it
    /// has read zero on every instrument for the life of the system. That mattered more than one
    /// unused field: every one of the thirteen entry rules reads the same OHLCV series, which is a
    /// structural reason they move together. Measured on 2026-09-02 the seven traded pairs had a
    /// mean pairwise correlation of 0.709 -- about 1.33 independent bets held as if they were seven
    /// -- and depth is the only evidence available that is not derived from price.
    ///
    /// Crypto only. The free equity feed carries no depth, and asking for one would either fail or,
    /// worse, return something partial that looked like a book.
    ///
    /// A failure here degrades the evidence and never stops the lane. Depth is a candidate
    /// predictor that has not been shown to survive its own costs, so it has no business halting a
    /// decision that does not depend on it.
    /// </summary>
    private async Task RefreshOrderBookAsync(
        OpportunityRoute route, int slot, long eventNs, CancellationToken cancellationToken)
    {
        if (orderBooks is null) return;
        if (route.AssetClass is not TradedAssetClass.SpotCrypto) return;

        try
        {
            BookImbalance book = await orderBooks.GetImbalanceAsync(route.Symbol, cancellationToken);
            if (!book.IsMeasurable) return;

            marketState.Apply(new OrderBookEvent(
                eventNs,
                slot,
                BestBid: 0d,
                BestAsk: 0d,
                BidDepth: (double)book.BidDepth,
                AskDepth: (double)book.AskDepth,
                eventNs,
                clock.MonotonicTimestamp,
                eventNs));
        }
        catch (Exception exception) when (HostedServiceFaults.IsFault(exception, cancellationToken))
        {
            logger.LogDebug(exception, "Could not read the order book for {Symbol}.", route.Symbol);
        }
    }

    /// <summary>
    /// Symbols the account currently holds through this system, from the durable records.
    ///
    /// Every lane's positions, not this lane's: correlation is a property of the account. Read from
    /// the store for the same reason the mechanism counts are -- it survives a restart, and it is
    /// the same history the evidence will be computed from.
    /// </summary>
    private IReadOnlyList<string> HeldSymbols() =>
    [
        .. spotStore.ListNonterminal()
            .Where(record => record.EntryFilledQuantity > 0m)
            .Select(record => record.Symbol)
            .Distinct(StringComparer.OrdinalIgnoreCase),
    ];

    private decimal HeldQuantity(string brokerSymbol) =>
        spotStore.ListNonterminal()
            .Where(record => BrokerSymbol.Matches(record.Symbol, brokerSymbol))
            .Sum(record => record.InternalOpenQuantity);

    private async Task<bool> IsSessionClosedAsync(string symbol, CancellationToken cancellationToken)
    {
        if (!router.TryRoute(symbol, out OpportunityRoute? route, out _)) return false;
        if (route?.AssetClass is TradedAssetClass.SpotCrypto) return false;

        try
        {
            return !(await marketClock.GetSessionAsync(cancellationToken)).IsOpen;
        }
        catch (Exception exception) when (HostedServiceFaults.IsFault(exception, cancellationToken))
        {
            logger.LogDebug(exception, "Could not read the venue session clock.");
            return false;
        }
    }

    /// <summary>
    /// Concurrent open positions per strategy mechanism, from the durable records.
    ///
    /// Read from the store rather than tracked in memory for the same reason the rotation's counts
    /// are: it survives a restart, and it is the same history the evidence will be computed from.
    /// </summary>
    private IReadOnlyDictionary<string, int> OpenPositionsByMechanism()
    {
        Dictionary<string, string> mechanisms = SignalStrategies.ForCrypto
            .Concat(SignalStrategies.ForEquity)
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Mechanism, StringComparer.Ordinal);

        var open = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (SpotExecutionRecord record in spotStore.ListNonterminal())
        {
            if (record.EntryFilledQuantity <= 0m) continue;
            if (!mechanisms.TryGetValue(record.StrategyId, out string? mechanism)) continue;
            open[mechanism] = open.GetValueOrDefault(mechanism) + 1;
        }

        return open;
    }

    private double? MeasuredCostUpperBoundBps() =>
        realisedCosts.Current()?.UpperConfidenceCostBpsFor(options.OrderNotional) is { } bps
            ? (double)bps
            : null;

    /// <summary>
    /// Marks every open position at the current mid, for the cost estimator's sibling arithmetic.
    ///
    /// A position that cannot be priced is marked at zero rather than omitted. Omitting it would
    /// make the sibling set look as though it had changed between the two readings, and the
    /// estimator refuses on exactly that -- so a missing quote would silently cost a measurement
    /// rather than visibly degrade one.
    /// </summary>
    private IReadOnlyList<PositionMark> MarkOpenPositions(
        IReadOnlyList<BrokerPositionSnapshot> positions)
    {
        if (positions.Count == 0) return [];

        List<PositionMark> marks = new(positions.Count);
        foreach (BrokerPositionSnapshot position in positions)
            marks.Add(new PositionMark(position.Symbol, position.Quantity, heldMarker.CurrentMid(position.Symbol) ?? 0m));

        return marks;
    }

    private static PortfolioSnapshot EmptyPortfolio(BrokerAccountSnapshot account) => new(
        0, new Usd(account.Equity), new Usd(account.Equity), new Usd(account.BuyingPower),
        Usd.Zero, Usd.Zero, Usd.Zero, Usd.Zero, 0, 0, 0, 0, 0, 0, 0, []);

    private async Task WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        state.Update("waiting_for_runtime", options.Symbol);
        while (runtimeMode.Snapshot().Mode != SystemMode.Ready && options.Mode != AutonomousTradingMode.ExperimentalPaper)
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
    }

    private async Task WaitUntilFlatAsync(int slot, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.FillTimeout);
        while (true)
        {
            IReadOnlyList<BrokerPositionSnapshot> positions = await broker.ListPositionsAsync(timeout.Token);
            if (positions.All(position => position.InstrumentSlot != slot || position.Quantity == 0)) return;
            await Task.Delay(TimeSpan.FromMilliseconds(250), timeout.Token);
        }
    }

    /// <summary>
    /// Last-resort close. Reports every way it can fail, including the quiet one.
    ///
    /// A broker refusal comes back as a rejected <see cref="BrokerSubmitResult"/> rather than an
    /// exception, so discarding the return value meant a refused emergency close looked identical to a
    /// successful one — silence in the logs and an open position nobody was told about. On the path
    /// that exists precisely for when everything else has failed, that is the worst possible default.
    /// </summary>
    private async Task EmergencyCloseAsync(int slot, CancellationToken cancellationToken)
    {
        try
        {
            BrokerSubmitResult result = await broker.ClosePositionAsync(slot, cancellationToken);
            if (result.State == BrokerSubmitState.Acknowledged) return;
            logger.LogCritical(
                "Emergency paper position close for {Symbol} was not acknowledged: state={State}, reason={Reason}, brokerRequestId={RequestId}. The position may still be open.",
                options.Symbol,
                result.State,
                result.ReasonCode ?? "none",
                result.RequestId ?? "none");
        }
        catch (Exception exception) when (HostedServiceFaults.IsFault(exception, cancellationToken))
        {
            logger.LogCritical(exception, "Emergency paper position close failed for {Symbol}.", options.Symbol);
        }
    }

    private static void EnsureAcknowledged(BrokerSubmitResult result, string stage)
    {
        if (result.State != BrokerSubmitState.Acknowledged || string.IsNullOrWhiteSpace(result.BrokerOrderId))
            throw new InvalidOperationException($"Autonomous {stage} order was not acknowledged by the paper broker.");
    }
}
