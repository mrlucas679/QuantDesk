using System.Diagnostics;
using QuantDesk.Alpaca.Capabilities;
using QuantDesk.Domain.Capabilities;
using QuantDesk.Alpaca.Mapping;
using QuantDesk.Alpaca.MarketData;
using QuantDesk.Domain.Execution;
using QuantDesk.Domain.Contracts;
using QuantDesk.Domain.Numerics;
using QuantDesk.Domain.Portfolio;
using QuantDesk.Domain.Runtime;
using QuantDesk.Domain.Strategies;
using QuantDesk.Domain.Trading;
using QuantDesk.Runtime.Execution;
using QuantDesk.Runtime.Modes;
using QuantDesk.Runtime.Portfolio;
using QuantDesk.Runtime.Positions;
using QuantDesk.Runtime.Reconciliation;
using QuantDesk.Runtime.Persistence;
using QuantDesk.Runtime.Reservations;
using QuantDesk.Runtime.Time;

namespace QuantDesk.Api.PaperTrading;

/// <summary>Supervises autonomous paper opportunities through the deterministic runtime.</summary>
public sealed class AutonomousPaperTradingService(
    IBrokerExecutionGateway broker,
    IInstrumentSymbolResolver symbols,
    IMarketEvidenceProvider evidenceProvider,
    BrokerExposureAttributor attributor,
    OpportunityRouter router,
    OptionExecutionCoordinator optionExecution,
    SpotExecutionLifecycle spotExecution,
    IAlpacaCapabilityProbe capabilityProbe,
    AutonomousDecisionPipeline pipeline,
    ResearchArtifactState researchArtifacts,
    ExitEngine exitEngine,
    AutonomousPaperTradingOptions options,
    RuntimeModeState runtimeMode,
    AutonomousTradingState state,
    IRuntimeClock clock,
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
        try
        {
            await EvaluateOpportunityAsync(stoppingToken);
        }
        catch (Exception exception) when (HostedServiceFaults.IsFault(exception, stoppingToken))
        {
            state.Update("abstained", options.Symbol, reason: "EvidenceUnavailable");
            logger.LogWarning(
                exception,
                "Autonomous cycle abstained for {Symbol}: market evidence was unavailable. The lane continues.",
                options.Symbol);
        }
    }

    internal async Task EvaluateOpportunityAsync(CancellationToken cancellationToken)
    {
        bool experimental = options.Mode == AutonomousTradingMode.ExperimentalPaper;
        if (runtimeMode.Snapshot().Mode != SystemMode.Ready && !experimental)
        {
            state.Update("entry_halted", options.Symbol, reason: "RuntimeNotReady");
            return;
        }
        // Route first: an unsupported symbol must never inherit another asset class's cost model,
        // order policy, or permission check. Routing also precedes slot resolution, because an
        // unroutable symbol is a configuration abstention, not a runtime fault — throwing here
        // would trip the catch-all and degrade the whole runtime over a bad setting.
        if (!router.TryRoute(options.Symbol, out OpportunityRoute? route, out string routeReason) ||
            route is null)
        {
            state.Update("abstained", options.Symbol, reason: routeReason);
            return;
        }

        if (!symbols.TryResolveBySymbol(options.Symbol, out int slot))
        {
            state.Update("abstained", options.Symbol, reason: "SymbolNotMappedToInstrumentSlot");
            logger.LogWarning(
                "Autonomous symbol {Symbol} routed to {AssetClass} but is not mapped to an instrument slot.",
                options.Symbol, route.AssetClass);
            return;
        }

        CapabilityReport probed = await capabilityProbe.ProbeAsync(cancellationToken);
        var capabilities = new AccountCapabilities(
            probed.PaperEnvironment, probed.EquityTrading, probed.CryptoTrading,
            probed.OptionsTrading, probed.OptionsTradingLevel);
        if (!route.IsPermittedBy(capabilities))
        {
            state.Update("abstained", options.Symbol, reason: "AssetClassNotPermitted");
            logger.LogWarning(
                "Autonomous route {AssetClass} is not permitted by the live account.", route.AssetClass);
            return;
        }
        ResearchArtifactSnapshot research = researchArtifacts.Snapshot();
        ForecastSnapshotContract? forecast = research.Forecast;
        if (!experimental && (!research.Ready || forecast is null))
        {
            state.Update("abstained", options.Symbol, reason: "VerifiedForecastUnavailable");
            return;
        }
        if (!experimental && (!string.Equals(forecast!.Instrument, options.Symbol, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(forecast.ForecastFamily, "directional_return_bps", StringComparison.OrdinalIgnoreCase) ||
            research.StrategyDefinition is null ||
            !string.Equals(research.StrategyDefinition.Symbol, options.Symbol, StringComparison.OrdinalIgnoreCase))
            )
        {
            state.Update("abstained", options.Symbol, reason: "VerifiedForecastIncompatible");
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
            state.Update("entry_halted", options.Symbol, reason: "PortfolioUnreconciled");
            logger.LogWarning(
                "Entry halted: {Attribution}. Nothing will trade until this is explained or closed.",
                attribution.Describe());
            return;
        }

        // Attributed exposure in the instrument we are about to trade is still disqualifying: a second
        // position in the same symbol would trade over the lane that already holds it.
        if (attribution.IsClaimed(route.Symbol))
        {
            state.Update("abstained", options.Symbol, reason: "SymbolAlreadyHeld");
            return;
        }

        PortfolioSnapshot initial = EmptyPortfolio(account);
        DirectionalMarketEvidence evidence = await evidenceProvider.GetEvidenceAsync(route, cancellationToken);
        AutonomousPipelineDecision decision = pipeline.Evaluate(
            slot, evidence, initial, true, true, capabilities,
            experimental ? null : (double)forecast!.PointForecast,
            experimental ? null : research.StrategyFamily,
            experimental ? null : research.StrategyDefinition);
        if (!decision.Approved || decision.Candidate is not TradeCandidate candidate ||
            decision.Risk is not { Approved: true } risk)
        {
            state.Update("abstained", options.Symbol, reason: decision.Reason,
                grossEdgeBps: decision.Committee is { } committee
                    ? (decimal)committee.ExpectedReturnBps
                    : null);
            logger.LogInformation("Autonomous decision for {Symbol}: {Reason}.", options.Symbol, decision.Reason);
            return;
        }

        // Refuse an order too small to pay its own costs. Fixed venue charges do not shrink with
        // order size, so below a certain notional the broker takes more than the edge can win and
        // the trade loses whichever way the market moves.
        decimal grossEdgeBps = decision.Committee is { } edge
            ? (decimal)Math.Abs(edge.ExpectedReturnBps)
            : 0m;
        if (!route.Costs.IsEconomicallyViable(
                options.OrderNotional, grossEdgeBps, spreadBps: 0m, out string viability))
        {
            state.Update("abstained", options.Symbol, reason: viability);
            logger.LogInformation(
                "Autonomous opportunity for {Symbol} refused as uneconomic at {Notional}: {Reason}. " +
                "Minimum viable notional is {Minimum}.",
                options.Symbol, options.OrderNotional, viability,
                route.Costs.MinimumViableNotionalUsd(grossEdgeBps));
            return;
        }

        // A defined-risk vertical expresses the same directional view the pipeline just approved,
        // using options instead of the underlying. The view is formed on the underlying above;
        // only the instrument differs, so the branch happens here and not earlier.
        if (options.Expression == OpportunityExpression.DefinedRiskVertical)
        {
            await ExecuteOptionOpportunityAsync(capabilities, candidate, decision, cancellationToken);
            return;
        }

        await ExecuteSpotOpportunityAsync(candidate, decision, evidence, slot, cancellationToken);
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
        TradeCandidate candidate,
        AutonomousPipelineDecision decision,
        DirectionalMarketEvidence evidence,
        int slot,
        CancellationToken cancellationToken)
    {
        decimal quantity = decimal.Round(
            options.OrderNotional / evidence.Ask, 8, MidpointRounding.ToZero);
        if (quantity <= 0)
        {
            // Rounding to zero means the notional cannot buy a tradable unit at this price.
            state.Update("abstained", options.Symbol, reason: "QuantityRoundedToZero");
            return;
        }

        string executionId = DeterministicClientOrderId.Create(
            "autospot", OpportunityIdentity(candidate, slot), "execution");
        decimal definedMaximumLoss = decision.Risk is { } risk && risk.RequiredRiskReservation.Value > 0
            ? risk.RequiredRiskReservation.Value
            : options.OrderNotional;

        if (!spotExecution.TryReserve(
                executionId, candidate.StrategyId, options.Symbol, slot, quantity,
                definedMaximumLoss, candidate.ManagementPlan.MaximumHoldingPeriod))
        {
            state.Update("abstained", options.Symbol, reason: "ReservationRejected");
            return;
        }

        state.Update("submitting_entry", options.Symbol, filledQuantity: quantity, reason: "Approved");
        SpotExecutionRecord record = await spotExecution.AdvanceAsync(executionId, cancellationToken);

        if (record.State is SpotExecutionState.Failed)
        {
            state.Update("abstained", options.Symbol, reason: record.FailureReason ?? "EntryFailed");
            logger.LogWarning(
                "Autonomous spot entry {ExecutionId} failed: {Reason}.",
                executionId, record.FailureReason);
            return;
        }

        state.Update("holding", options.Symbol, record.EntryBrokerOrderId,
            filledQuantity: record.EntryFilledQuantity, reason: record.State.ToString());
        logger.LogInformation(
            "Autonomous spot entry {ClientOrderId} submitted for {Symbol} at quantity {Quantity}; " +
            "the recovery worker owns fills, the hold, and the exit.",
            record.EntryClientOrderId, options.Symbol, quantity);
    }

    private async Task ManagePositionAsync(
        TradeCandidate candidate, OpportunityRoute route, int slot, BrokerOrderSnapshot entryFill,
        PortfolioLedger portfolio, TradeUpdateProcessor updates, ExecutionWorker execution,
        ReservationLedger reservations, PortfolioReservation reservation,
        CancellationToken cancellationToken)
    {
        long openedTicks = clock.MonotonicTimestamp;
        while (!cancellationToken.IsCancellationRequested)
        {
            DirectionalMarketEvidence evidence = await evidenceProvider.GetEvidenceAsync(route, cancellationToken);
            portfolio.MarkToMarket(new Dictionary<int, decimal> { [slot] = (evidence.Bid + evidence.Ask) / 2m });
            PositionSnapshot position = portfolio.Snapshot().Positions.Single(item => item.InstrumentSlot == slot);
            bool thesisValid = HasCurrentVerifiedForecast();
            ExitEvaluation exit = exitEngine.Evaluate(candidate.ManagementPlan, openedTicks,
                clock.MonotonicTimestamp, position.UnrealizedPnl,
                thesisValid, regimeValid: true);
            if (exit.ShouldExit)
            {
                await SubmitManagedExitAsync(candidate, slot, entryFill, portfolio, updates, execution,
                    reservations, reservation, exit, cancellationToken);
                return;
            }
            await Task.Delay(PositionMonitorInterval, cancellationToken);
        }
    }

    private bool HasCurrentVerifiedForecast()
    {
        ResearchArtifactSnapshot research = researchArtifacts.Snapshot();
        return research.Ready && research.Forecast is { } forecast &&
            string.Equals(forecast.Instrument, options.Symbol, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(forecast.ForecastFamily, "directional_return_bps", StringComparison.OrdinalIgnoreCase) &&
            forecast.PointForecast > 0;
    }

    /// <summary>
    /// Routes an approved directional view into the durable multi-leg options lifecycle.
    ///
    /// Risk, capital, and reconciliation checks have already run above; the coordinator adds the
    /// options-specific ones — permission for the asset class, an admissible spread, and a debit
    /// that stays inside the risk budget — and commits the reservation durably before any POST.
    /// </summary>
    private async Task ExecuteOptionOpportunityAsync(
        AccountCapabilities capabilities,
        TradeCandidate candidate,
        AutonomousPipelineDecision decision,
        CancellationToken cancellationToken)
    {
        double expectedReturnBps = decision.Committee?.ExpectedReturnBps ?? 0d;
        decimal underlyingPrice = decision.Market is { } market && market.Mid > 0
            ? (decimal)market.Mid
            : 0m;
        string executionId = DeterministicClientOrderId.Create(
            "autoopt", OpportunityIdentity(candidate, candidate.InstrumentSlot), "execution");

        state.Update("submitting_entry", options.Symbol, reason: "OptionSpreadAdmitted");
        OptionExecutionOutcome outcome = await optionExecution.ExecuteAsync(
            options.Symbol,
            capabilities,
            executionId,
            underlyingPrice,
            expectedReturnBps,
            options.OrderNotional,
            candidate.ManagementPlan,
            clock.UtcNow,
            MinimumOptionDaysToExpiry,
            MaximumOptionDaysToExpiry,
            OptionStrikeBandFraction,
            cancellationToken);

        if (!outcome.Submitted)
        {
            state.Update("abstained", options.Symbol, reason: outcome.Reason);
            logger.LogInformation(
                "Autonomous option opportunity for {Symbol} was not submitted: {Reason}.",
                options.Symbol, outcome.Reason);
            return;
        }

        // The multi-leg recovery worker owns the record from here: fills, the durable hold, the
        // managed exit, and final reconciliation all advance without this cycle holding state.
        state.Update("holding", options.Symbol, reason: outcome.State?.ToString());
        logger.LogInformation(
            "Autonomous option entry {ClientOrderId} submitted for {Symbol}; " +
            "defined maximum loss {Loss}, net debit {Debit}. Lifecycle owns the exit.",
            outcome.EntryClientOrderId, options.Symbol,
            outcome.DefinedMaximumLoss, outcome.NetDebitPerSpread);
    }

    /// <summary>
    /// The reproducible identity of one opportunity. Every component is already persisted with the
    /// candidate, so the same opportunity yields the same client-order IDs on a later pass.
    /// </summary>
    private string OpportunityIdentity(TradeCandidate candidate, int slot) =>
        string.Join(
            '|',
            options.Symbol.Trim().ToUpperInvariant(),
            slot.ToString(System.Globalization.CultureInfo.InvariantCulture),
            candidate.StrategyId,
            candidate.CandidateId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            candidate.SourceStateVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private async Task SubmitManagedExitAsync(
        TradeCandidate candidate, int slot, BrokerOrderSnapshot entryFill, PortfolioLedger portfolio,
        TradeUpdateProcessor updates, ExecutionWorker execution, ReservationLedger reservations,
        PortfolioReservation reservation, ExitEvaluation exit, CancellationToken cancellationToken)
    {
        string exitClientId = DeterministicClientOrderId.Create(
            "auto", OpportunityIdentity(candidate, slot), "exit");
        ExecutionIntent exitIntent = CreateReservedIntent(candidate, reservation, exitClientId);
        portfolio.RegisterOrderAttribution(exitClientId,
            new OrderAttribution(candidate.StrategyId, candidate.ManagementPlan.ExitPolicyVersion,
                candidate.CandidateId, 1, []));
        ExecutionCommand exitCommand = CreateCommand(candidate, reservation.ReservationId, exitClientId,
            entryFill.FilledQuantity, OrderSide.Sell, PositionIntent.Close, ExecutionPriority.NormalExit);
        state.Update("submitting_exit", options.Symbol, entryFill.BrokerOrderId,
            filledQuantity: entryFill.FilledQuantity, reason: exit.Reason.ToString());
        BrokerSubmitResult submitted = await execution.SubmitOneAsync(
            exitIntent, exitCommand, clock.MonotonicTimestamp, cancellationToken);
        EnsureAcknowledged(submitted, "exit");
        BrokerOrderSnapshot exitFill = await WaitForFillAsync(exitClientId, submitted.BrokerOrderId!, cancellationToken);
        ApplyFill(exitIntent, execution, updates, exitFill, slot, OrderSide.Sell);
        exitIntent.TransitionTo(ExecutionIntentState.Completed);
        reservations.Release(reservation.ReservationId);
        await WaitUntilFlatAsync(slot, cancellationToken);

        var reconciliation = new ReconciliationService(runtimeMode).Reconcile(new ReconciliationInput(
            new HashSet<string>(StringComparer.Ordinal) { entryFill.ClientOrderId, exitFill.ClientOrderId },
            new Dictionary<int, decimal>(), await broker.ListOpenOrdersAsync(cancellationToken),
            await broker.ListPositionsAsync(cancellationToken)));
        if (!reconciliation.IsReconciled)
            throw new InvalidOperationException("Broker and portfolio truth diverged after managed exit.");
        state.Update("completed_flat", options.Symbol, entryFill.BrokerOrderId, exitFill.BrokerOrderId,
            entryFill.FilledQuantity, exit.Reason.ToString());
    }

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

    private static PortfolioSnapshot EmptyPortfolio(BrokerAccountSnapshot account) => new(
        0, new Usd(account.Equity), new Usd(account.Equity), new Usd(account.BuyingPower),
        Usd.Zero, Usd.Zero, Usd.Zero, Usd.Zero, 0, 0, 0, 0, 0, 0, 0, []);

    private async Task<BrokerOrderSnapshot> WaitForFillAsync(
        string clientOrderId, string brokerOrderId, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.FillTimeout);
        try
        {
            while (true)
            {
                BrokerOrderSnapshot? order = await broker.FindByClientOrderIdAsync(clientOrderId, timeout.Token);
                if (order is not null && string.Equals(order.Status, "filled", StringComparison.OrdinalIgnoreCase) &&
                    order.FilledQuantity > 0) return order;
                if (order is not null && order.Status is "canceled" or "expired" or "rejected" or "suspended")
                    throw new InvalidOperationException("Paper order reached a non-fill terminal state.");
                await Task.Delay(TimeSpan.FromMilliseconds(250), timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await broker.CancelAsync(brokerOrderId, cancellationToken);
            throw new TimeoutException("Paper order did not fill before its deadline.");
        }
    }

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
