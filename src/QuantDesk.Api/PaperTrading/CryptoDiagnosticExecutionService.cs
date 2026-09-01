using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QuantDesk.Alpaca.Mapping;
using QuantDesk.Domain.Execution;
using QuantDesk.Domain.Trading;
using QuantDesk.Runtime.Persistence;
using QuantDesk.Runtime.Time;

namespace QuantDesk.Api.PaperTrading;

/// <summary>Runs a bounded, infrastructure-only BTC/USD paper-entry diagnostic.</summary>
public sealed class CryptoDiagnosticExecutionService(
    FullSystemReadinessState readiness,
    DiagnosticExecutionStore store,
    IBrokerExecutionGateway broker,
    DiagnosticExecutionOptions options,
    IInstrumentSymbolResolver symbols,
    IRuntimeClock clock)
{
    public async Task<DiagnosticExecutionResult> PrepareAsync(
        string experimentId,
        string symbol,
        decimal notional,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(experimentId) || string.IsNullOrWhiteSpace(symbol) || notional <= 0)
            return DiagnosticExecutionResult.Blocked("INVALID_DIAGNOSTIC_REQUEST");
        if (!options.Allows(symbol, notional))
            return DiagnosticExecutionResult.Blocked("DIAGNOSTIC_RISK_ENVELOPE_EXCEEDED");

        DiagnosticExecutionResult? infrastructureFailure = VerifyLocalInfrastructure();
        if (infrastructureFailure is not null) return infrastructureFailure;
        if (!store.IsAvailable()) return DiagnosticExecutionResult.Blocked("PERSISTENCE_UNAVAILABLE");

        string normalizedExperimentId = experimentId.Trim();
        try
        {
            if (store.Find(normalizedExperimentId) is not null)
                return DiagnosticExecutionResult.Blocked("DUPLICATE_DIAGNOSTIC_RESERVATION");
        }
        catch (Exception exception) when (IsPersistenceFailure(exception))
        {
            return DiagnosticExecutionResult.Blocked("PERSISTENCE_UNAVAILABLE");
        }

        BrokerEntryContext context;
        try
        {
            context = await ReadBrokerEntryContextAsync(
                entryClientOrderId: null,
                cancellationToken);
        }
        catch (Exception exception) when (IsInfrastructureFailure(exception, cancellationToken))
        {
            return DiagnosticExecutionResult.Blocked("RECONCILIATION_UNAVAILABLE");
        }

        DiagnosticExecutionResult? brokerFailure = VerifyBrokerAdmission(context, notional);
        if (brokerFailure is not null) return brokerFailure;
        if (context.OpenOrders.Count != 0 || RelevantPositions(context.Positions, instrumentSlot: null).Count != 0)
            return DiagnosticExecutionResult.Blocked("UNEXPLAINED_BROKER_EXPOSURE");

        return PersistReservation(normalizedExperimentId, notional);
    }

    private DiagnosticExecutionResult PersistReservation(string experimentId, decimal notional)
    {
        string entryId = ClientId(experimentId, "entry");
        string exitId = ClientId(experimentId, "exit");
        string emergencyId = ClientId(experimentId, "emergency");
        DateTimeOffset reservedAt = clock.UtcNow;
        var record = new DiagnosticExecutionRecord(
            experimentId,
            nameof(OrderClassification.DiagnosticExecution),
            DiagnosticExecutionOptions.RequiredSymbol,
            "EntryReserved",
            notional,
            DiagnosticExecutionOptions.HoldingDuration,
            reservedAt,
            entryId,
            exitId)
        {
            EntryReservedAt = reservedAt,
            EmergencyClientOrderId = emergencyId,
            ReconciliationResult = "Clean"
        };

        try
        {
            if (!store.TryCreateReservation(record, entryId, exitId, emergencyId))
                return DiagnosticExecutionResult.Blocked("DUPLICATE_DIAGNOSTIC_RESERVATION");
        }
        catch (Exception exception) when (IsPersistenceFailure(exception))
        {
            return DiagnosticExecutionResult.Blocked("PERSISTENCE_UNAVAILABLE");
        }

        return DiagnosticExecutionResult.Ready(experimentId, entryId, exitId);
    }

    public async Task<DiagnosticExecutionResult> AdvanceAsync(
        string experimentId,
        int instrumentSlot,
        decimal quantity,
        CancellationToken cancellationToken)
    {
        DiagnosticExecutionRecord? record;
        try
        {
            record = store.Find(experimentId);
        }
        catch (Exception exception) when (IsPersistenceFailure(exception))
        {
            return DiagnosticExecutionResult.Blocked("PERSISTENCE_UNAVAILABLE");
        }

        if (record is null) return DiagnosticExecutionResult.Blocked("DIAGNOSTIC_NOT_FOUND");
        if (!symbols.TryResolveBySymbol(DiagnosticExecutionOptions.RequiredSymbol, out instrumentSlot))
            return DiagnosticExecutionResult.Blocked("BTC_USD_INSTRUMENT_UNAVAILABLE");
        if (record.State == "Complete")
        {
            BackfillCompletedMetrics(record);
            return Ready(store.Find(record.ExperimentId)!);
        }
        if (record.State is "EmergencyFlattenReserved" or "EmergencyFlattenAccepted")
            return await EmergencyFlattenAsync(record.ExperimentId, instrumentSlot, cancellationToken);
        if (record.State is "Holding" or "EntryFilled") return AdvanceHolding(record);
        if (record.State == "Reconciling")
            return await ReconcileFinalAsync(record, instrumentSlot, cancellationToken);
        if (IsExitLifecycleState(record.State))
            return await AdvanceExitAsync(record, instrumentSlot, cancellationToken);
        if (IsTerminalEntryState(record.State)) return TerminalResult(record);
        if (!options.Allows(record.Symbol, record.RequestedNotional))
            return DiagnosticExecutionResult.Blocked("DIAGNOSTIC_RISK_ENVELOPE_EXCEEDED");

        DiagnosticExecutionResult? infrastructureFailure = VerifyLocalInfrastructure();
        if (infrastructureFailure is not null) return infrastructureFailure;
        if (!store.IsAvailable()) return DiagnosticExecutionResult.Blocked("PERSISTENCE_UNAVAILABLE");

        BrokerEntryContext context;
        try
        {
            context = await ReadBrokerEntryContextAsync(record.EntryClientOrderId, cancellationToken);
        }
        catch (Exception exception) when (IsInfrastructureFailure(exception, cancellationToken))
        {
            return DiagnosticExecutionResult.Blocked("RECONCILIATION_UNAVAILABLE");
        }

        DiagnosticExecutionResult? brokerFailure = VerifyBrokerAdmission(context, record.RequestedNotional);
        if (brokerFailure is not null) return brokerFailure;
        try
        {
            return await ContinueEntryAsync(record, context, instrumentSlot, quantity, cancellationToken);
        }
        catch (Exception exception) when (IsPersistenceFailure(exception))
        {
            return DiagnosticExecutionResult.Blocked("PERSISTENCE_UNAVAILABLE");
        }
    }

    private async Task<DiagnosticExecutionResult> ContinueEntryAsync(
        DiagnosticExecutionRecord record,
        BrokerEntryContext context,
        int instrumentSlot,
        decimal quantity,
        CancellationToken cancellationToken)
    {
        // Once the deterministic entry order exists, broker exposure created by that
        // order is explained. Attach its latest state before comparing persisted
        // fills with the position; crypto fees can make those quantities differ.
        if (context.ExistingOrder is not null)
            return PersistBrokerOrder(record, context.ExistingOrder);

        if (!IsReconciled(record, context, instrumentSlot))
        {
            PersistReconciliationMismatch(record.ExperimentId);
            return DiagnosticExecutionResult.Blocked("UNEXPLAINED_BROKER_EXPOSURE");
        }

        store.Update(record.ExperimentId, current => current with { ReconciliationResult = "Clean" });
        if (record.EntrySubmissionAttemptedAt is not null)
            return DiagnosticExecutionResult.Blocked("ENTRY_SUBMISSION_UNKNOWN");
        if (quantity <= 0) return Ready(record);

        DateTimeOffset attemptedAt = clock.UtcNow;
        if (!store.TryClaimEntrySubmission(record.ExperimentId, quantity, attemptedAt, out DiagnosticExecutionRecord? claimed))
            return await RecoverClaimedSubmissionAsync(record, cancellationToken);

        ExecutionCommand command = DiagnosticCommandFactory.Entry(claimed!, instrumentSlot, quantity, clock.UtcNow);
        return await SubmitEntryAsync(claimed!, command, cancellationToken);
    }

    private DiagnosticExecutionResult AdvanceHolding(DiagnosticExecutionRecord record)
    {
        if (record.FinalEntryFillAt is not DateTimeOffset finalFillAt)
            return DiagnosticExecutionResult.Blocked("ENTRY_FINAL_FILL_TIME_MISSING");

        DateTimeOffset scheduledExitAt = finalFillAt.Add(DiagnosticExecutionOptions.HoldingDuration);
        try
        {
            store.Update(record.ExperimentId, current => current with
            {
                State = clock.UtcNow < scheduledExitAt ? "Holding" : "ExitDue",
                HoldStartedAt = finalFillAt,
                ScheduledExitAt = scheduledExitAt
            });
            return Ready(store.Find(record.ExperimentId)!);
        }
        catch (Exception exception) when (IsPersistenceFailure(exception))
        {
            return DiagnosticExecutionResult.Blocked("PERSISTENCE_UNAVAILABLE");
        }
    }

    private async Task<DiagnosticExecutionResult> AdvanceExitAsync(
        DiagnosticExecutionRecord record,
        int instrumentSlot,
        CancellationToken cancellationToken)
    {
        if (IsTerminalExitState(record.State)) return TerminalResult(record);
        DiagnosticExecutionResult? infrastructureFailure = VerifyLocalInfrastructure();
        if (infrastructureFailure is not null) return infrastructureFailure;
        if (!store.IsAvailable()) return DiagnosticExecutionResult.Blocked("PERSISTENCE_UNAVAILABLE");

        BrokerEntryContext context;
        try
        {
            context = await ReadBrokerEntryContextAsync(record.ExitClientOrderId, cancellationToken);
        }
        catch (Exception exception) when (IsInfrastructureFailure(exception, cancellationToken))
        {
            return DiagnosticExecutionResult.Blocked("RECONCILIATION_UNAVAILABLE");
        }

        DiagnosticExecutionResult? brokerFailure = VerifyBrokerExitAdmission(context);
        if (brokerFailure is not null) return brokerFailure;
        try
        {
            return await ContinueExitAsync(record, context, instrumentSlot, cancellationToken);
        }
        catch (Exception exception) when (IsPersistenceFailure(exception))
        {
            return DiagnosticExecutionResult.Blocked("PERSISTENCE_UNAVAILABLE");
        }
    }

    private async Task<DiagnosticExecutionResult> ContinueExitAsync(
        DiagnosticExecutionRecord record,
        BrokerEntryContext context,
        int instrumentSlot,
        CancellationToken cancellationToken)
    {
        if (HasUnknownOrder(record, context.OpenOrders))
        {
            PersistReconciliationMismatch(record.ExperimentId);
            return DiagnosticExecutionResult.Blocked("UNEXPLAINED_BROKER_EXPOSURE");
        }

        decimal brokerPositionQuantity = RelevantPositions(context.Positions, instrumentSlot)
            .Sum(position => position.Quantity);
        if (context.ExistingOrder is not null)
        {
            DiagnosticExecutionResult tracked = PersistExitBrokerOrder(
                record,
                context.ExistingOrder,
                brokerPositionQuantity);
            DiagnosticExecutionRecord persisted = store.Find(record.ExperimentId)!;
            return persisted.State == "Reconciling"
                ? await ReconcileFinalAsync(persisted, instrumentSlot, cancellationToken)
                : tracked;
        }
        if (record.ExitSubmissionAttemptedAt is not null)
            return DiagnosticExecutionResult.Blocked("EXIT_SUBMISSION_UNKNOWN");

        DiagnosticExecutionRecord? reserved = ReserveExitFromBrokerTruth(record, brokerPositionQuantity);
        if (reserved is null)
            return DiagnosticExecutionResult.Blocked("BTC_USD_POSITION_UNAVAILABLE");
        if (reserved.ExitQuantity != brokerPositionQuantity)
            return DiagnosticExecutionResult.Blocked("EXIT_POSITION_CHANGED");

        if (!store.TryClaimExitSubmission(
                record.ExperimentId,
                clock.UtcNow,
                out DiagnosticExecutionRecord? claimed))
            return await RecoverClaimedExitSubmissionAsync(record, cancellationToken);

        return await SubmitExitAsync(
            claimed!,
            DiagnosticCommandFactory.Exit(claimed!, instrumentSlot, clock.UtcNow),
            cancellationToken);
    }

    private DiagnosticExecutionRecord? ReserveExitFromBrokerTruth(
        DiagnosticExecutionRecord record,
        decimal brokerPositionQuantity)
    {
        if (record.State == "ExitReserved") return record;
        if (record.State != "ExitDue" || brokerPositionQuantity <= 0) return null;
        return store.TryReserveExit(
            record.ExperimentId,
            brokerPositionQuantity,
            clock.UtcNow,
            out DiagnosticExecutionRecord? reserved)
            ? reserved
            : store.Find(record.ExperimentId);
    }

    private async Task<DiagnosticExecutionResult> SubmitExitAsync(
        DiagnosticExecutionRecord record,
        ExecutionCommand command,
        CancellationToken cancellationToken)
    {
        BrokerSubmitResult submitted;
        try
        {
            submitted = await broker.SubmitAsync(command, cancellationToken);
        }
        catch (Exception exception) when (IsAmbiguousSubmission(exception, cancellationToken))
        {
            return await RecoverAmbiguousExitSubmissionAsync(record, cancellationToken);
        }

        if (submitted.State == BrokerSubmitState.Rejected)
        {
            store.Update(record.ExperimentId, current => current with
            {
                State = "ExitRejected",
                Failure = DiagnosticExecutionFailure.ExitRejected,
                FailureReason = submitted.ReasonCode ?? "EXIT_REJECTED"
            });
            return DiagnosticExecutionResult.Blocked(submitted.ReasonCode ?? "EXIT_REJECTED");
        }
        if (submitted.State == BrokerSubmitState.Unknown || string.IsNullOrWhiteSpace(submitted.BrokerOrderId))
            return await RecoverAmbiguousExitSubmissionAsync(record, cancellationToken);

        store.Update(record.ExperimentId, current => current with
        {
            State = "ExitAccepted",
            ExitBrokerOrderId = submitted.BrokerOrderId,
            ExitSubmittedAt = current.ExitSubmittedAt ?? clock.UtcNow,
            ExitAcknowledgedAt = current.ExitAcknowledgedAt ?? clock.UtcNow,
            Failure = DiagnosticExecutionFailure.None,
            FailureReason = null
        });
        return Ready(record);
    }

    private async Task<DiagnosticExecutionResult> RecoverAmbiguousExitSubmissionAsync(
        DiagnosticExecutionRecord record,
        CancellationToken cancellationToken)
    {
        BrokerOrderSnapshot? existing;
        try
        {
            existing = await broker.FindByClientOrderIdAsync(record.ExitClientOrderId!, cancellationToken);
        }
        catch (Exception exception) when (IsInfrastructureFailure(exception, cancellationToken))
        {
            existing = null;
        }

        if (existing is not null) return await RecoverExitOrderAsync(record, existing, cancellationToken);
        store.Update(record.ExperimentId, current => current with
        {
            State = "ExitSubmissionUnknown",
            Failure = DiagnosticExecutionFailure.SubmissionUnknown,
            FailureReason = "EXIT_SUBMISSION_UNKNOWN"
        });
        return DiagnosticExecutionResult.Blocked("EXIT_SUBMISSION_UNKNOWN");
    }

    private async Task<DiagnosticExecutionResult> RecoverClaimedExitSubmissionAsync(
        DiagnosticExecutionRecord record,
        CancellationToken cancellationToken)
    {
        BrokerOrderSnapshot? existing;
        try
        {
            existing = await broker.FindByClientOrderIdAsync(record.ExitClientOrderId!, cancellationToken);
        }
        catch (Exception exception) when (IsInfrastructureFailure(exception, cancellationToken))
        {
            return DiagnosticExecutionResult.Blocked("RECONCILIATION_UNAVAILABLE");
        }

        return existing is null
            ? DiagnosticExecutionResult.Blocked("EXIT_SUBMISSION_UNKNOWN")
            : await RecoverExitOrderAsync(record, existing, cancellationToken);
    }

    private async Task<DiagnosticExecutionResult> RecoverExitOrderAsync(
        DiagnosticExecutionRecord record,
        BrokerOrderSnapshot order,
        CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<BrokerPositionSnapshot> positions = await broker.ListPositionsAsync(cancellationToken);
            decimal brokerQuantity = RelevantPositions(positions, instrumentSlot: null)
                .Sum(position => position.Quantity);
            DiagnosticExecutionResult tracked = PersistExitBrokerOrder(record, order, brokerQuantity);
            DiagnosticExecutionRecord persisted = store.Find(record.ExperimentId)!;
            return persisted.State == "Reconciling"
                ? await ReconcileFinalAsync(persisted, instrumentSlot: 0, cancellationToken)
                : tracked;
        }
        catch (Exception exception) when (IsInfrastructureFailure(exception, cancellationToken))
        {
            return DiagnosticExecutionResult.Blocked("RECONCILIATION_UNAVAILABLE");
        }
    }

    private async Task<DiagnosticExecutionResult> ReconcileFinalAsync(
        DiagnosticExecutionRecord record,
        int instrumentSlot,
        CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<BrokerOrderSnapshot> openOrders = await broker.ListOpenOrdersForSymbolAsync(
                DiagnosticExecutionOptions.RequiredSymbol,
                cancellationToken);
            IReadOnlyList<BrokerPositionSnapshot> positions = await broker.ListPositionsAsync(cancellationToken);
            decimal brokerQuantity = RelevantPositions(positions, instrumentSlot).Sum(position => position.Quantity);
            decimal internalQuantity = InternalExposure(record);
            bool hasUnresolvedDiagnosticOrder = openOrders.Any(order => IsDiagnosticOrder(record, order));
            bool reconciled = !hasUnresolvedDiagnosticOrder && brokerQuantity == 0 && internalQuantity == 0;

            store.Update(record.ExperimentId, current => current with
            {
                State = reconciled ? "Complete" : "ReconciliationFailed",
                FinalBrokerQuantity = brokerQuantity,
                FinalInternalQuantity = internalQuantity,
                ReconciliationResult = reconciled ? "Flat" : "Mismatch",
                GrossPaperPnl = reconciled ? ComputeGrossPaperPnl(current) : current.GrossPaperPnl,
                CompletedAt = reconciled ? clock.UtcNow : null,
                Failure = reconciled
                    ? DiagnosticExecutionFailure.None
                    : DiagnosticExecutionFailure.ReconciliationFailed,
                FailureReason = reconciled ? null : ReconciliationFailureReason(
                    hasUnresolvedDiagnosticOrder,
                    brokerQuantity,
                    internalQuantity)
            });

            DiagnosticExecutionRecord persisted = store.Find(record.ExperimentId)!;
            return reconciled ? Ready(persisted) : TerminalResult(persisted);
        }
        catch (Exception exception) when (IsInfrastructureFailure(exception, cancellationToken))
        {
            return DiagnosticExecutionResult.Blocked("RECONCILIATION_UNAVAILABLE");
        }
    }

    public async Task<DiagnosticExecutionResult> EmergencyFlattenAsync(
        string experimentId,
        int instrumentSlot,
        CancellationToken cancellationToken)
    {
        DiagnosticExecutionRecord? record = store.Find(experimentId);
        if (record is null) return DiagnosticExecutionResult.Blocked("DIAGNOSTIC_NOT_FOUND");
        if (!symbols.TryResolveBySymbol(DiagnosticExecutionOptions.RequiredSymbol, out instrumentSlot))
            return DiagnosticExecutionResult.Blocked("BTC_USD_INSTRUMENT_UNAVAILABLE");
        if (!broker.IsPaperEnvironment) return DiagnosticExecutionResult.Blocked("ALPACA_PAPER_REQUIRED");
        if (string.IsNullOrWhiteSpace(record.EmergencyClientOrderId))
            return DiagnosticExecutionResult.Blocked("EMERGENCY_CLIENT_ID_UNAVAILABLE");

        try
        {
            BrokerOrderSnapshot? existing = await broker.FindByClientOrderIdAsync(
                record.EmergencyClientOrderId,
                cancellationToken);
            if (existing is not null)
                return await TrackEmergencyOrderAsync(record, existing, instrumentSlot, cancellationToken);

            IReadOnlyList<BrokerOrderSnapshot> openOrders = await broker.ListOpenOrdersForSymbolAsync(
                DiagnosticExecutionOptions.RequiredSymbol,
                cancellationToken);
            foreach (BrokerOrderSnapshot order in openOrders.Where(order => IsDiagnosticOrder(record, order)))
                await broker.CancelAsync(order.BrokerOrderId, cancellationToken);

            IReadOnlyList<BrokerOrderSnapshot> remainingOrders = await broker.ListOpenOrdersForSymbolAsync(
                DiagnosticExecutionOptions.RequiredSymbol,
                cancellationToken);
            if (remainingOrders.Any(order => IsDiagnosticOrder(record, order)))
                return FailEmergency(record, "EMERGENCY_CANCEL_UNCONFIRMED");

            IReadOnlyList<BrokerPositionSnapshot> positions = await broker.ListPositionsAsync(cancellationToken);
            decimal brokerQuantity = RelevantPositions(positions, instrumentSlot).Sum(position => position.Quantity);
            if (brokerQuantity == 0)
            {
                store.Update(experimentId, current => current with
                {
                    State = "Reconciling"
                });
                return await ReconcileFinalAsync(store.Find(experimentId)!, instrumentSlot, cancellationToken);
            }
            if (brokerQuantity < 0) return FailEmergency(record, "EMERGENCY_SHORT_EXPOSURE_UNSUPPORTED");
            if (record.EmergencySubmissionAttemptedAt is not null)
                return FailEmergency(record, "EMERGENCY_SUBMISSION_UNKNOWN");

            if (!store.TryClaimEmergencySubmission(
                    experimentId,
                    brokerQuantity,
                    clock.UtcNow,
                    out DiagnosticExecutionRecord? claimed))
                return DiagnosticExecutionResult.Blocked("EMERGENCY_SUBMISSION_UNKNOWN");

            return await SubmitEmergencyFlattenAsync(claimed!, instrumentSlot, cancellationToken);
        }
        catch (Exception exception) when (IsInfrastructureFailure(exception, cancellationToken))
        {
            return DiagnosticExecutionResult.Blocked("RECONCILIATION_UNAVAILABLE");
        }
    }

    private async Task<DiagnosticExecutionResult> SubmitEmergencyFlattenAsync(
        DiagnosticExecutionRecord record,
        int instrumentSlot,
        CancellationToken cancellationToken)
    {
        try
        {
            BrokerSubmitResult result = await broker.SubmitAsync(
                DiagnosticCommandFactory.EmergencyFlatten(record, instrumentSlot, clock.UtcNow),
                cancellationToken);
            if (result.State == BrokerSubmitState.Rejected)
                return FailEmergency(record, result.ReasonCode ?? "EMERGENCY_REJECTED");
            if (result.State != BrokerSubmitState.Acknowledged || string.IsNullOrWhiteSpace(result.BrokerOrderId))
                return await RecoverEmergencySubmissionAsync(record, instrumentSlot, cancellationToken);

            store.Update(record.ExperimentId, current => current with
            {
                State = "EmergencyFlattenAccepted",
                EmergencyBrokerOrderId = result.BrokerOrderId,
                Failure = DiagnosticExecutionFailure.None,
                FailureReason = null
            });
            return Ready(store.Find(record.ExperimentId)!);
        }
        catch (Exception exception) when (IsAmbiguousSubmission(exception, cancellationToken))
        {
            return await RecoverEmergencySubmissionAsync(record, instrumentSlot, cancellationToken);
        }
    }

    private async Task<DiagnosticExecutionResult> RecoverEmergencySubmissionAsync(
        DiagnosticExecutionRecord record,
        int instrumentSlot,
        CancellationToken cancellationToken)
    {
        BrokerOrderSnapshot? existing = await broker.FindByClientOrderIdAsync(
            record.EmergencyClientOrderId!,
            cancellationToken);
        return existing is null
            ? FailEmergency(record, "EMERGENCY_SUBMISSION_UNKNOWN")
            : await TrackEmergencyOrderAsync(record, existing, instrumentSlot, cancellationToken);
    }

    private async Task<DiagnosticExecutionResult> TrackEmergencyOrderAsync(
        DiagnosticExecutionRecord record,
        BrokerOrderSnapshot order,
        int instrumentSlot,
        CancellationToken cancellationToken)
    {
        string status = order.Status.Trim().ToLowerInvariant();
        if (status is "rejected" or "canceled" or "expired")
            return FailEmergency(record, $"EMERGENCY_{status.ToUpperInvariant()}");

        store.Update(record.ExperimentId, current => current with
        {
            State = status == "filled" ? "Reconciling" : "EmergencyFlattenAccepted",
            EmergencyBrokerOrderId = order.BrokerOrderId,
            EmergencyFilledQuantity = order.FilledQuantity,
            Failure = DiagnosticExecutionFailure.None,
            FailureReason = null
        });
        DiagnosticExecutionRecord persisted = store.Find(record.ExperimentId)!;
        return status == "filled"
            ? await ReconcileFinalAsync(persisted, instrumentSlot, cancellationToken)
            : Ready(persisted);
    }

    private DiagnosticExecutionResult FailEmergency(DiagnosticExecutionRecord record, string reason)
    {
        store.Update(record.ExperimentId, current => current with
        {
            State = "EmergencyFlattenFailed",
            Failure = DiagnosticExecutionFailure.EmergencyFlattenFailed,
            FailureReason = reason
        });
        return DiagnosticExecutionResult.Blocked(reason);
    }

    private DiagnosticExecutionResult? VerifyLocalInfrastructure()
    {
        if (!broker.IsPaperEnvironment)
            return DiagnosticExecutionResult.Blocked("ALPACA_PAPER_REQUIRED");
        return readiness.Snapshot().InfrastructureExecutionReady
            ? null
            : DiagnosticExecutionResult.Blocked("INFRASTRUCTURE_NOT_READY");
    }

    private async Task<BrokerEntryContext> ReadBrokerEntryContextAsync(
        string? entryClientOrderId,
        CancellationToken cancellationToken)
    {
        BrokerAccountSnapshot? account = await broker.GetAccountAsync(cancellationToken);
        BrokerAssetSnapshot? asset = await broker.GetAssetAsync(
            DiagnosticExecutionOptions.RequiredSymbol,
            cancellationToken);
        BrokerOrderSnapshot? existing = entryClientOrderId is null
            ? null
            : await broker.FindByClientOrderIdAsync(entryClientOrderId, cancellationToken);
        IReadOnlyList<BrokerOrderSnapshot> orders = await broker.ListOpenOrdersForSymbolAsync(
            DiagnosticExecutionOptions.RequiredSymbol,
            cancellationToken);
        IReadOnlyList<BrokerPositionSnapshot> positions = await broker.ListPositionsAsync(cancellationToken);
        return new BrokerEntryContext(account, asset, existing, orders, positions);
    }

    private static DiagnosticExecutionResult? VerifyBrokerAdmission(BrokerEntryContext context, decimal notional)
    {
        BrokerAccountSnapshot? account = context.Account;
        if (account is null || account.TradingBlocked || account.AccountBlocked || account.Equity <= 0 ||
            account.BuyingPower < notional ||
            !string.Equals(account.Status, "ACTIVE", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(account.CryptoTradingStatus, "ACTIVE", StringComparison.OrdinalIgnoreCase))
            return DiagnosticExecutionResult.Blocked("PAPER_ACCOUNT_UNAVAILABLE");

        BrokerAssetSnapshot? asset = context.Asset;
        if (asset is null || !asset.Tradable ||
            !string.Equals(asset.Status, "active", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(asset.AssetClass, "crypto", StringComparison.OrdinalIgnoreCase) ||
            !SymbolsMatch(asset.Symbol, DiagnosticExecutionOptions.RequiredSymbol))
            return DiagnosticExecutionResult.Blocked("BTC_USD_NOT_TRADABLE");

        return null;
    }

    private static DiagnosticExecutionResult? VerifyBrokerExitAdmission(BrokerEntryContext context)
    {
        BrokerAccountSnapshot? account = context.Account;
        if (account is null || account.TradingBlocked || account.AccountBlocked ||
            !string.Equals(account.Status, "ACTIVE", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(account.CryptoTradingStatus, "ACTIVE", StringComparison.OrdinalIgnoreCase))
            return DiagnosticExecutionResult.Blocked("PAPER_ACCOUNT_UNAVAILABLE");

        BrokerAssetSnapshot? asset = context.Asset;
        return asset is not null && asset.Tradable &&
               string.Equals(asset.Status, "active", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(asset.AssetClass, "crypto", StringComparison.OrdinalIgnoreCase) &&
               SymbolsMatch(asset.Symbol, DiagnosticExecutionOptions.RequiredSymbol)
            ? null
            : DiagnosticExecutionResult.Blocked("BTC_USD_NOT_TRADABLE");
    }

    private static bool IsReconciled(
        DiagnosticExecutionRecord record,
        BrokerEntryContext context,
        int instrumentSlot)
    {
        if (HasUnknownOrder(record, context.OpenOrders)) return false;

        IReadOnlyList<BrokerPositionSnapshot> relevantPositions = RelevantPositions(context.Positions, instrumentSlot);
        decimal brokerQuantity = relevantPositions.Sum(position => position.Quantity);
        decimal explainedQuantity = context.ExistingOrder?.FilledQuantity ?? record.EntryFilledQuantity;
        return brokerQuantity == explainedQuantity;
    }

    private static bool HasUnknownOrder(
        DiagnosticExecutionRecord record,
        IReadOnlyList<BrokerOrderSnapshot> openOrders) => openOrders.Any(order =>
        !string.Equals(order.ClientOrderId, record.EntryClientOrderId, StringComparison.Ordinal) &&
        !string.Equals(order.ClientOrderId, record.ExitClientOrderId, StringComparison.Ordinal));

    private static IReadOnlyList<BrokerPositionSnapshot> RelevantPositions(
        IReadOnlyList<BrokerPositionSnapshot> positions,
        int? instrumentSlot) => positions
        .Where(position => SymbolsMatch(position.Symbol, DiagnosticExecutionOptions.RequiredSymbol))
        .Where(position => position.Quantity != 0)
        .ToArray();

    private async Task<DiagnosticExecutionResult> SubmitEntryAsync(
        DiagnosticExecutionRecord record,
        ExecutionCommand command,
        CancellationToken cancellationToken)
    {
        BrokerSubmitResult submitted;
        try
        {
            submitted = await broker.SubmitAsync(command, cancellationToken);
        }
        catch (Exception exception) when (IsAmbiguousSubmission(exception, cancellationToken))
        {
            return await RecoverAmbiguousSubmissionAsync(record, cancellationToken);
        }

        if (submitted.State == BrokerSubmitState.Rejected)
        {
            store.Update(record.ExperimentId, current => current with
            {
                State = "EntryRejected",
                Failure = DiagnosticExecutionFailure.EntryRejected,
                FailureReason = submitted.ReasonCode ?? "ENTRY_REJECTED"
            });
            return DiagnosticExecutionResult.Blocked(submitted.ReasonCode ?? "ENTRY_REJECTED");
        }
        if (submitted.State == BrokerSubmitState.Unknown || string.IsNullOrWhiteSpace(submitted.BrokerOrderId))
            return await RecoverAmbiguousSubmissionAsync(record, cancellationToken);

        store.Update(record.ExperimentId, current => current with
        {
            State = "EntryAccepted",
            EntryBrokerOrderId = submitted.BrokerOrderId,
            EntrySubmittedAt = current.EntrySubmittedAt ?? clock.UtcNow,
            EntryAcknowledgedAt = current.EntryAcknowledgedAt ?? clock.UtcNow,
            Failure = DiagnosticExecutionFailure.None,
            FailureReason = null
        });
        return Ready(record);
    }

    private async Task<DiagnosticExecutionResult> RecoverAmbiguousSubmissionAsync(
        DiagnosticExecutionRecord record,
        CancellationToken cancellationToken)
    {
        BrokerOrderSnapshot? existing;
        try
        {
            existing = await broker.FindByClientOrderIdAsync(record.EntryClientOrderId!, cancellationToken);
        }
        catch (Exception exception) when (IsInfrastructureFailure(exception, cancellationToken))
        {
            existing = null;
        }

        if (existing is not null) return PersistBrokerOrder(record, existing);
        store.Update(record.ExperimentId, current => current with
        {
            State = "EntrySubmissionUnknown",
            Failure = DiagnosticExecutionFailure.SubmissionUnknown,
            FailureReason = "ENTRY_SUBMISSION_UNKNOWN"
        });
        return DiagnosticExecutionResult.Blocked("ENTRY_SUBMISSION_UNKNOWN");
    }

    private async Task<DiagnosticExecutionResult> RecoverClaimedSubmissionAsync(
        DiagnosticExecutionRecord record,
        CancellationToken cancellationToken)
    {
        BrokerOrderSnapshot? existing;
        try
        {
            existing = await broker.FindByClientOrderIdAsync(record.EntryClientOrderId!, cancellationToken);
        }
        catch (Exception exception) when (IsInfrastructureFailure(exception, cancellationToken))
        {
            return DiagnosticExecutionResult.Blocked("RECONCILIATION_UNAVAILABLE");
        }

        return existing is null
            ? DiagnosticExecutionResult.Blocked("ENTRY_SUBMISSION_UNKNOWN")
            : PersistBrokerOrder(record, existing);
    }

    private DiagnosticExecutionResult PersistBrokerOrder(
        DiagnosticExecutionRecord record,
        BrokerOrderSnapshot order)
    {
        DateTimeOffset observedAt = clock.UtcNow;
        string status = order.Status.Trim().ToLowerInvariant();
        DateTimeOffset? finalEntryFillAt = status == "filled"
            ? order.FilledAt ?? order.UpdatedAt ?? observedAt
            : null;
        string state = status switch
        {
            "accepted" or "new" or "pending" or "pending_new" => "EntryAccepted",
            "partially_filled" => "EntryPartiallyFilled",
            "filled" => "Holding",
            "canceled" => "EntryCanceled",
            "rejected" => "EntryRejected",
            "expired" => "EntryExpired",
            _ => "EntrySubmissionUnknown"
        };
        DiagnosticExecutionFailure failure = state switch
        {
            "EntryCanceled" => DiagnosticExecutionFailure.EntryCanceled,
            "EntryRejected" => DiagnosticExecutionFailure.EntryRejected,
            "EntryExpired" => DiagnosticExecutionFailure.EntryExpired,
            "EntrySubmissionUnknown" => DiagnosticExecutionFailure.SubmissionUnknown,
            _ => DiagnosticExecutionFailure.None
        };

        store.Update(record.ExperimentId, current => current with
        {
            State = state,
            EntryBrokerOrderId = string.IsNullOrWhiteSpace(order.BrokerOrderId)
                ? current.EntryBrokerOrderId
                : order.BrokerOrderId,
            EntryFilledQuantity = order.FilledQuantity,
            EntryAverageFillPrice = order.AverageFillPrice ?? current.EntryAverageFillPrice,
            EntrySubmittedAt = order.SubmittedAt ?? current.EntrySubmittedAt ??
                               current.EntrySubmissionAttemptedAt ?? observedAt,
            EntryAcknowledgedAt = current.EntryAcknowledgedAt ?? order.SubmittedAt ?? observedAt,
            EntryBrokerCreatedAt = order.CreatedAt ?? current.EntryBrokerCreatedAt,
            EntryBrokerUpdatedAt = order.UpdatedAt ?? current.EntryBrokerUpdatedAt,
            EntryBrokerCanceledAt = order.CanceledAt ?? current.EntryBrokerCanceledAt,
            EntryBrokerExpiredAt = order.ExpiredAt ?? current.EntryBrokerExpiredAt,
            EntryBrokerRejectedAt = order.RejectedAt ?? current.EntryBrokerRejectedAt,
            FirstEntryFillAt = order.FilledQuantity > 0
                ? current.FirstEntryFillAt ?? order.FilledAt ?? order.UpdatedAt ?? observedAt
                : current.FirstEntryFillAt,
            FinalEntryFillAt = finalEntryFillAt ?? current.FinalEntryFillAt,
            HoldStartedAt = finalEntryFillAt ?? current.HoldStartedAt,
            ScheduledExitAt = finalEntryFillAt?.Add(DiagnosticExecutionOptions.HoldingDuration) ??
                              current.ScheduledExitAt,
            Failure = failure,
            FailureReason = failure == DiagnosticExecutionFailure.None ? null : $"ENTRY_{status.ToUpperInvariant()}"
        });

        DiagnosticExecutionRecord persisted = store.Find(record.ExperimentId)!;
        return IsTerminalEntryState(state) ? TerminalResult(persisted) : Ready(persisted);
    }

    private DiagnosticExecutionResult PersistExitBrokerOrder(
        DiagnosticExecutionRecord record,
        BrokerOrderSnapshot order,
        decimal brokerPositionQuantity)
    {
        DateTimeOffset observedAt = clock.UtcNow;
        string status = order.Status.Trim().ToLowerInvariant();
        DateTimeOffset? finalExitFillAt = status == "filled"
            ? order.FilledAt ?? order.UpdatedAt ?? observedAt
            : null;
        string state = status switch
        {
            "accepted" or "new" or "pending" or "pending_new" => "ExitAccepted",
            "partially_filled" => "ExitPartiallyFilled",
            "filled" => "Reconciling",
            "canceled" => "ExitCanceled",
            "rejected" => "ExitRejected",
            "expired" => "ExitExpired",
            _ => "ExitSubmissionUnknown"
        };
        DiagnosticExecutionFailure failure = state switch
        {
            "ExitCanceled" => DiagnosticExecutionFailure.ExitCanceled,
            "ExitRejected" => DiagnosticExecutionFailure.ExitRejected,
            "ExitExpired" => DiagnosticExecutionFailure.ExitExpired,
            "ExitSubmissionUnknown" => DiagnosticExecutionFailure.SubmissionUnknown,
            _ => DiagnosticExecutionFailure.None
        };

        store.Update(record.ExperimentId, current => current with
        {
            State = state,
            ExitBrokerOrderId = string.IsNullOrWhiteSpace(order.BrokerOrderId)
                ? current.ExitBrokerOrderId
                : order.BrokerOrderId,
            ExitFilledQuantity = order.FilledQuantity,
            ExitAverageFillPrice = order.AverageFillPrice ?? current.ExitAverageFillPrice,
            ExitSubmittedAt = order.SubmittedAt ?? current.ExitSubmittedAt ??
                              current.ExitSubmissionAttemptedAt ?? observedAt,
            ExitAcknowledgedAt = current.ExitAcknowledgedAt ?? order.SubmittedAt ?? observedAt,
            ExitBrokerCreatedAt = order.CreatedAt ?? current.ExitBrokerCreatedAt,
            ExitBrokerUpdatedAt = order.UpdatedAt ?? current.ExitBrokerUpdatedAt,
            ExitBrokerCanceledAt = order.CanceledAt ?? current.ExitBrokerCanceledAt,
            ExitBrokerExpiredAt = order.ExpiredAt ?? current.ExitBrokerExpiredAt,
            ExitBrokerRejectedAt = order.RejectedAt ?? current.ExitBrokerRejectedAt,
            FirstExitFillAt = order.FilledQuantity > 0
                ? current.FirstExitFillAt ?? order.FilledAt ?? order.UpdatedAt ?? observedAt
                : current.FirstExitFillAt,
            FinalExitFillAt = finalExitFillAt ?? current.FinalExitFillAt,
            FinalBrokerQuantity = brokerPositionQuantity,
            FinalInternalQuantity = Math.Max(0, current.ExitQuantity - order.FilledQuantity),
            ReconciliationResult = state == "Reconciling" ? "Pending" : current.ReconciliationResult,
            CompletedAt = null,
            Failure = failure,
            FailureReason = failure == DiagnosticExecutionFailure.None ? null : $"EXIT_{status.ToUpperInvariant()}"
        });

        DiagnosticExecutionRecord persisted = store.Find(record.ExperimentId)!;
        return IsTerminalExitState(state) ? TerminalResult(persisted) : Ready(persisted);
    }

    private void PersistReconciliationMismatch(string experimentId)
    {
        store.Update(experimentId, current => current with
        {
            ReconciliationResult = "Mismatch",
            Failure = DiagnosticExecutionFailure.ReconciliationMismatch,
            FailureReason = "UNEXPLAINED_BROKER_EXPOSURE"
        });
    }

    private static bool IsDiagnosticOrder(DiagnosticExecutionRecord record, BrokerOrderSnapshot order) =>
        string.Equals(order.ClientOrderId, record.EntryClientOrderId, StringComparison.Ordinal) ||
        string.Equals(order.ClientOrderId, record.ExitClientOrderId, StringComparison.Ordinal) ||
        string.Equals(order.ClientOrderId, record.EmergencyClientOrderId, StringComparison.Ordinal);

    private static string ReconciliationFailureReason(
        bool unresolvedOrders,
        decimal brokerQuantity,
        decimal internalQuantity)
    {
        if (unresolvedOrders) return "RECONCILIATION_UNRESOLVED_DIAGNOSTIC_ORDERS";
        if (brokerQuantity != internalQuantity) return "RECONCILIATION_BROKER_INTERNAL_MISMATCH";
        if (brokerQuantity != 0) return "RECONCILIATION_BROKER_EXPOSURE_REMAINS";
        return "RECONCILIATION_INTERNAL_EXPOSURE_REMAINS";
    }

    private static decimal InternalExposure(DiagnosticExecutionRecord record)
    {
        decimal flattenBasis = record.ExitQuantity > 0
            ? record.ExitQuantity
            : record.EntryFilledQuantity;
        return Math.Max(0, flattenBasis - record.ExitFilledQuantity - record.EmergencyFilledQuantity);
    }

    private void BackfillCompletedMetrics(DiagnosticExecutionRecord record)
    {
        if (record.GrossPaperPnl is not null) return;
        decimal? grossPaperPnl = ComputeGrossPaperPnl(record);
        if (grossPaperPnl is null) return;
        store.Update(record.ExperimentId, current => current with { GrossPaperPnl = grossPaperPnl });
    }

    private static decimal? ComputeGrossPaperPnl(DiagnosticExecutionRecord record)
    {
        if (record.EntryAverageFillPrice is not decimal entryPrice ||
            record.ExitAverageFillPrice is not decimal exitPrice ||
            record.ExitFilledQuantity <= 0)
            return null;
        return (exitPrice - entryPrice) * record.ExitFilledQuantity;
    }

    private static bool SymbolsMatch(string left, string right) => string.Equals(
        left.Replace("/", string.Empty, StringComparison.Ordinal),
        right.Replace("/", string.Empty, StringComparison.Ordinal),
        StringComparison.OrdinalIgnoreCase);

    private static bool IsTerminalEntryState(string state) =>
        state is "EntryCanceled" or "EntryRejected" or "EntryExpired" or
            "ReconciliationFailed" or "EmergencyFlattenFailed";

    private static bool IsExitLifecycleState(string state) => state is
        "ExitDue" or "ExitReserved" or "ExitSubmitted" or "ExitAccepted" or "ExitPartiallyFilled" or
        "ExitFilled" or "ExitSubmissionUnknown" or "ExitCanceled" or "ExitRejected" or "ExitExpired";

    private static bool IsTerminalExitState(string state) =>
        state is "ExitCanceled" or "ExitRejected" or "ExitExpired";

    private static DiagnosticExecutionResult TerminalResult(DiagnosticExecutionRecord record) =>
        DiagnosticExecutionResult.Blocked(record.FailureReason ?? record.State.ToUpperInvariant());

    private static DiagnosticExecutionResult Ready(DiagnosticExecutionRecord record) =>
        DiagnosticExecutionResult.Ready(
            record.ExperimentId,
            record.EntryClientOrderId!,
            record.ExitClientOrderId!);

    private static bool IsAmbiguousSubmission(Exception exception, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested &&
        exception is TimeoutException or TaskCanceledException or HttpRequestException or IOException;

    private static bool IsInfrastructureFailure(Exception exception, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested &&
        exception is TimeoutException or TaskCanceledException or HttpRequestException or IOException or JsonException;

    private static bool IsPersistenceFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or
            JsonException or NotSupportedException;

    private static string ClientId(string experimentId, string leg)
    {
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(experimentId)))
            .ToLowerInvariant()[..12];
        return $"qd-diag-{digest}-{leg}";
    }

    private sealed record BrokerEntryContext(
        BrokerAccountSnapshot? Account,
        BrokerAssetSnapshot? Asset,
        BrokerOrderSnapshot? ExistingOrder,
        IReadOnlyList<BrokerOrderSnapshot> OpenOrders,
        IReadOnlyList<BrokerPositionSnapshot> Positions);
}

public sealed record DiagnosticExecutionResult(
    bool Allowed,
    string Reason,
    string? ExperimentId,
    string? EntryClientOrderId,
    string? ExitClientOrderId)
{
    public static DiagnosticExecutionResult Blocked(string reason) => new(false, reason, null, null, null);
    public static DiagnosticExecutionResult Ready(string id, string entry, string exit) =>
        new(true, "READY", id, entry, exit);
}
