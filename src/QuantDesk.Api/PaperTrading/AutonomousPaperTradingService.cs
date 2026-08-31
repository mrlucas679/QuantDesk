using System.Diagnostics;
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
using QuantDesk.Runtime.Reservations;
using QuantDesk.Runtime.Time;

namespace QuantDesk.Api.PaperTrading;

/// <summary>Supervises autonomous paper opportunities through the deterministic runtime.</summary>
public sealed class AutonomousPaperTradingService(
    IBrokerExecutionGateway broker,
    IInstrumentSymbolResolver symbols,
    AlpacaLatestCryptoQuoteClient quoteClient,
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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) { state.Update("disabled", options.Symbol); return; }
        try
        {
            await WaitUntilReadyAsync(stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await EvaluateOpportunityAsync(stoppingToken);
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

    private async Task EvaluateOpportunityAsync(CancellationToken cancellationToken)
    {
        bool experimental = options.Mode == AutonomousTradingMode.ExperimentalPaper;
        if (runtimeMode.Snapshot().Mode != SystemMode.Ready && !experimental)
        {
            state.Update("entry_halted", options.Symbol, reason: "RuntimeNotReady");
            return;
        }
        if (!symbols.TryResolveBySymbol(options.Symbol, out int slot))
            throw new InvalidOperationException("Autonomous symbol is not mapped to an instrument slot.");
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
        if (openOrders.Count != 0 || brokerPositions.Count != 0)
        {
            runtimeMode.Transition(SystemMode.EntryHalted, "broker_state_requires_reconciliation");
            state.Update("entry_halted", options.Symbol, reason: "PortfolioUnreconciled");
            return;
        }

        PortfolioSnapshot initial = EmptyPortfolio(account);
        CryptoMarketEvidence evidence = await quoteClient.GetEvidenceAsync(options.Symbol, cancellationToken);
        AutonomousPipelineDecision decision = pipeline.Evaluate(
            slot, evidence, initial, true, true,
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

        var reservations = new ReservationLedger(initial);
        if (!reservations.TryReserve(initial.Version, risk.RequiredRiskReservation,
                risk.RequiredCapitalReservation, new Usd(options.OrderNotional), out PortfolioReservation? reservation) ||
            reservation is null)
        {
            state.Update("abstained", options.Symbol, reason: "ReservationRejected");
            return;
        }

        var portfolio = new PortfolioLedger(initial);
        var updates = new TradeUpdateProcessor(portfolio);
        var execution = new ExecutionWorker(broker, reservations, runtimeMode, options.FillTimeout);
        string entryClientId = $"qd-auto-entry-{Guid.NewGuid():N}";
        ExecutionIntent entryIntent = CreateReservedIntent(candidate, reservation, entryClientId);
        portfolio.RegisterOrderAttribution(entryClientId,
            new OrderAttribution(candidate.StrategyId, candidate.ManagementPlan.ExitPolicyVersion,
                candidate.CandidateId, 1, decision.Committee?.SupportingExperts.ToArray() ?? []));

        decimal quantity = decimal.Round(options.OrderNotional / evidence.Ask, 8, MidpointRounding.ToZero);
        if (quantity <= 0) throw new InvalidOperationException("Calculated autonomous quantity is zero.");
        ExecutionCommand entryCommand = CreateCommand(candidate, reservation.ReservationId, entryClientId,
            quantity, OrderSide.Buy, PositionIntent.Open, ExecutionPriority.ExploitationEntry);
        state.Update("submitting_entry", options.Symbol, filledQuantity: quantity, reason: "Approved");
        BrokerSubmitResult submitted = await execution.SubmitOneAsync(
            entryIntent, entryCommand, clock.MonotonicTimestamp, cancellationToken);
        EnsureAcknowledged(submitted, "entry");
        BrokerOrderSnapshot entryFill = await WaitForFillAsync(entryClientId, submitted.BrokerOrderId!, cancellationToken);
        ApplyFill(entryIntent, execution, updates, entryFill, slot, OrderSide.Buy);
        entryIntent.TransitionTo(ExecutionIntentState.PositionManaging);
        state.Update("position_managing", options.Symbol, submitted.BrokerOrderId,
            filledQuantity: entryFill.FilledQuantity, reason: candidate.ManagementPlan.ExitPolicyVersion);

        try
        {
            await ManagePositionAsync(candidate, slot, entryFill, portfolio, updates, execution,
                reservations, reservation, cancellationToken);
        }
        catch
        {
            runtimeMode.Transition(SystemMode.RiskReductionOnly, "position_management_failure");
            await EmergencyCloseAsync(slot, cancellationToken);
            throw;
        }
    }

    private async Task ManagePositionAsync(
        TradeCandidate candidate, int slot, BrokerOrderSnapshot entryFill, PortfolioLedger portfolio,
        TradeUpdateProcessor updates, ExecutionWorker execution, ReservationLedger reservations,
        PortfolioReservation reservation, CancellationToken cancellationToken)
    {
        long openedTicks = clock.MonotonicTimestamp;
        while (!cancellationToken.IsCancellationRequested)
        {
            CryptoMarketEvidence evidence = await quoteClient.GetEvidenceAsync(options.Symbol, cancellationToken);
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

    private async Task SubmitManagedExitAsync(
        TradeCandidate candidate, int slot, BrokerOrderSnapshot entryFill, PortfolioLedger portfolio,
        TradeUpdateProcessor updates, ExecutionWorker execution, ReservationLedger reservations,
        PortfolioReservation reservation, ExitEvaluation exit, CancellationToken cancellationToken)
    {
        string exitClientId = $"qd-auto-exit-{Guid.NewGuid():N}";
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

    private async Task EmergencyCloseAsync(int slot, CancellationToken cancellationToken)
    {
        try { await broker.ClosePositionAsync(slot, cancellationToken); }
        catch (Exception exception) when (exception is not OperationCanceledException)
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
