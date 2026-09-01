using QuantDesk.Domain.Execution;
using QuantDesk.Domain.Trading;
using QuantDesk.Runtime.Persistence;
using QuantDesk.Runtime.Time;

namespace QuantDesk.Runtime.Execution;

/// <summary>
/// Owns one spot opportunity from reservation through managed exit, durably.
///
/// The autonomous spot lane kept this state in memory, so a restart between reserving and filling
/// forgot that an order existed. Deterministic client order IDs alone could not fix that: an ID you
/// can recompute is useless if nothing recorded the opportunity. This persists the reservation
/// before any POST, claims the right to submit atomically so two callers cannot both send, and
/// resolves an ambiguous submission by asking the broker for the exact ID it would have used —
/// never by generating a replacement.
/// </summary>
public sealed class SpotExecutionLifecycle(
    IBrokerExecutionGateway broker,
    SpotExecutionStore store,
    IRuntimeClock clock,
    TimeSpan brokerSubmitTimeout,
    IHoldInterrupt? holdInterrupt = null)
{
    /// <summary>
    /// Persists the reservation. Nothing reaches the broker until this returns true, so an
    /// interrupted run always leaves a record naming the order that may exist.
    /// </summary>
    public bool TryReserve(
        string executionId,
        string strategyId,
        string symbol,
        int instrumentSlot,
        decimal quantity,
        decimal definedMaximumLoss,
        TimeSpan maximumHoldingPeriod,
        decimal? entryLimitPrice = null,
        decimal? exitLimitPrice = null,
        PositionOwnership? ownership = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);

        if (!broker.IsPaperEnvironment || !store.IsAvailable()) return false;
        if (quantity <= 0 || definedMaximumLoss <= 0 || maximumHoldingPeriod <= TimeSpan.Zero) return false;

        // Refuse a second concurrent execution on the same symbol. The caller's broker-position
        // check cannot cover this on its own: between submitting an order and that order becoming
        // a visible position there is a window in which a fresh evaluation would look at a flat
        // account and open a second position. The durable record closes that window, because it
        // exists from the moment of reservation.
        string normalizedSymbol = symbol.Trim().ToUpperInvariant();
        if (store.ListNonterminal().Any(existing =>
                string.Equals(existing.Symbol, normalizedSymbol, StringComparison.Ordinal)))
            return false;

        DateTimeOffset now = clock.UtcNow;
        return store.TryCreate(new SpotExecutionRecord(
            executionId,
            strategyId,
            normalizedSymbol,
            instrumentSlot,
            SpotExecutionState.EntryReserved,
            DeterministicClientOrderId.Create("spot", executionId, "entry"),
            DeterministicClientOrderId.Create("spot", executionId, "exit"),
            quantity,
            now,
            now)
        {
            DefinedMaximumLoss = definedMaximumLoss,
            MaximumHoldingPeriod = maximumHoldingPeriod,
            EntryLimitPrice = entryLimitPrice,
            ExitLimitPrice = exitLimitPrice,
            Ownership = ownership
        });
    }

    /// <summary>Advances one execution by exactly one step, returning the persisted record.</summary>
    public async Task<SpotExecutionRecord> AdvanceAsync(
        string executionId, CancellationToken cancellationToken)
    {
        SpotExecutionRecord record = store.Find(executionId)
            ?? throw new InvalidOperationException($"Spot execution '{executionId}' does not exist.");
        if (record.IsTerminal) return record;

        return record.State switch
        {
            SpotExecutionState.EntryReserved => await SubmitEntryAsync(record, cancellationToken),
            SpotExecutionState.EntrySubmitted or SpotExecutionState.EntryAccepted
                or SpotExecutionState.EntryPartiallyFilled =>
                await TrackEntryAsync(record, cancellationToken),
            SpotExecutionState.EntryFilled => StartHold(record),
            SpotExecutionState.Holding => EvaluateHold(record),
            SpotExecutionState.ExitDue or SpotExecutionState.ExitReserved =>
                await SubmitExitAsync(record, cancellationToken),
            SpotExecutionState.ExitSubmitted or SpotExecutionState.ExitAccepted
                or SpotExecutionState.ExitPartiallyFilled =>
                await TrackExitAsync(record, cancellationToken),
            SpotExecutionState.ExitFilled or SpotExecutionState.Reconciling =>
                await ReconcileAsync(record, cancellationToken),
            _ => record
        };
    }

    /// <summary>Resumes every nonterminal execution. Called on startup and on a timer.</summary>
    public async Task RecoverAllAsync(CancellationToken cancellationToken)
    {
        foreach (SpotExecutionRecord record in store.ListNonterminal())
            await AdvanceAsync(record.ExecutionId, cancellationToken);
    }

    private async Task<SpotExecutionRecord> SubmitEntryAsync(
        SpotExecutionRecord record, CancellationToken cancellationToken)
    {
        // Claim first. If a previous attempt already claimed, fall through to recovery rather than
        // sending a second order for the same opportunity.
        if (!store.TryClaimEntrySubmission(record.ExecutionId, clock.UtcNow, out SpotExecutionRecord? claimed) ||
            claimed is null)
            return await RecoverByClientOrderIdAsync(record, record.EntryClientOrderId, entry: true, cancellationToken);

        var command = new ExecutionCommand(
            CommandId: 1,
            ExecutionPriority.ExploitationEntry,
            RiskReservationId: 0,
            CapitalReservationId: 0,
            claimed.EntryClientOrderId,
            claimed.InstrumentSlot,
            OrderSide.Buy,
            PositionIntent.Open,
            claimed.EntryLimitPrice is > 0 ? ExecutionOrderType.Limit : ExecutionOrderType.Market,
            ExecutionTimeInForce.Ioc,
            claimed.Quantity,
            claimed.EntryLimitPrice,
            clock.MonotonicTimestamp,
            clock.MonotonicTimestamp,
            claimed.StrategyId);

        BrokerSubmitResult result;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(brokerSubmitTimeout);
            result = await broker.SubmitAsync(command, timeout.Token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException ||
                                          !cancellationToken.IsCancellationRequested)
        {
            // Ambiguous: the order may or may not exist. Never generate a replacement ID.
            return await RecoverByClientOrderIdAsync(
                claimed, claimed.EntryClientOrderId, entry: true, cancellationToken);
        }

        SpotExecutionRecord updated = result.State switch
        {
            BrokerSubmitState.Acknowledged => claimed with
            {
                State = SpotExecutionState.EntryAccepted,
                EntrySubmittedAt = clock.UtcNow,
                EntryBrokerOrderId = result.BrokerOrderId
            },
            BrokerSubmitState.Rejected => claimed with
            {
                State = SpotExecutionState.Failed,
                FailureReason = result.ReasonCode ?? "ENTRY_REJECTED",
                CompletedAt = clock.UtcNow
            },
            _ => claimed with { State = SpotExecutionState.EntrySubmitted }
        };
        store.Update(updated);
        return updated;
    }

    private async Task<SpotExecutionRecord> TrackEntryAsync(
        SpotExecutionRecord record, CancellationToken cancellationToken)
    {
        BrokerOrderSnapshot? order =
            await broker.FindByClientOrderIdAsync(record.EntryClientOrderId, cancellationToken);
        if (order is null) return record;

        SpotExecutionRecord updated = record with
        {
            EntryBrokerOrderId = order.BrokerOrderId,
            EntryFilledQuantity = order.FilledQuantity,
            EntryAverageFillPrice = order.AverageFillPrice
        };

        updated = order.Status.ToLowerInvariant() switch
        {
            "filled" => updated with
            {
                State = SpotExecutionState.EntryFilled,
                EntryFinalFillAt = clock.UtcNow
            },
            "partially_filled" => updated with { State = SpotExecutionState.EntryPartiallyFilled },
            "canceled" or "expired" or "rejected" => updated with
            {
                // A cancelled entry that partially filled still left exposure, so it must be
                // exited rather than abandoned as failed.
                State = updated.EntryFilledQuantity > 0
                    ? SpotExecutionState.EntryFilled
                    : SpotExecutionState.Failed,
                FailureReason = updated.EntryFilledQuantity > 0 ? null : $"ENTRY_{order.Status.ToUpperInvariant()}",
                CompletedAt = updated.EntryFilledQuantity > 0 ? null : clock.UtcNow,
                EntryFinalFillAt = updated.EntryFilledQuantity > 0 ? clock.UtcNow : null
            },
            _ => updated with { State = SpotExecutionState.EntryAccepted }
        };

        store.Update(updated);
        return updated;
    }

    private SpotExecutionRecord StartHold(SpotExecutionRecord record)
    {
        DateTimeOffset now = clock.UtcNow;
        SpotExecutionRecord updated = record with
        {
            State = SpotExecutionState.Holding,
            HoldStartedAt = record.HoldStartedAt ?? now,
            ScheduledExitAt = record.ScheduledExitAt ?? now.Add(record.MaximumHoldingPeriod)
        };
        store.Update(updated);
        return updated;
    }

    private SpotExecutionRecord EvaluateHold(SpotExecutionRecord record)
    {
        // The scheduled exit is durable, so a restart mid-hold still exits at the original time
        // rather than restarting the clock.
        // The scheduled time and the interrupts are consulted together, and an interrupt can only
        // bring the exit forward. A faulty interrupt therefore cannot extend a hold past the
        // deadline the reservation was taken against; the timer stays the backstop it always was.
        bool timerDue = record.ScheduledExitAt is { } due && clock.UtcNow >= due;
        HoldInterrupt interrupt = timerDue
            ? HoldInterrupt.None
            : holdInterrupt?.Evaluate(record) ?? HoldInterrupt.None;

        if (!timerDue && !interrupt.ShouldExitNow) return record;

        SpotExecutionRecord updated = record with
        {
            State = SpotExecutionState.ExitDue,
            EarlyExitReason = interrupt.Reason,
        };
        store.Update(updated);
        return updated;
    }

    private async Task<SpotExecutionRecord> SubmitExitAsync(
        SpotExecutionRecord record, CancellationToken cancellationToken)
    {
        if (record.InternalOpenQuantity <= 0)
        {
            SpotExecutionRecord nothingToExit = record with { State = SpotExecutionState.Reconciling };
            store.Update(nothingToExit);
            return nothingToExit;
        }

        if (!store.TryClaimExitSubmission(record.ExecutionId, clock.UtcNow, out SpotExecutionRecord? claimed) ||
            claimed is null)
            return await RecoverByClientOrderIdAsync(record, record.ExitClientOrderId, entry: false, cancellationToken);

        var command = new ExecutionCommand(
            CommandId: 2,
            ExecutionPriority.RiskReduction,
            RiskReservationId: 0,
            CapitalReservationId: 0,
            claimed.ExitClientOrderId,
            claimed.InstrumentSlot,
            OrderSide.Sell,
            PositionIntent.Close,
            claimed.ExitLimitPrice is > 0 ? ExecutionOrderType.Limit : ExecutionOrderType.Market,
            ExecutionTimeInForce.Ioc,
            claimed.InternalOpenQuantity,
            claimed.ExitLimitPrice,
            clock.MonotonicTimestamp,
            clock.MonotonicTimestamp,
            claimed.StrategyId);

        BrokerSubmitResult result;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(brokerSubmitTimeout);
            result = await broker.SubmitAsync(command, timeout.Token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException ||
                                          !cancellationToken.IsCancellationRequested)
        {
            return await RecoverByClientOrderIdAsync(
                claimed, claimed.ExitClientOrderId, entry: false, cancellationToken);
        }

        SpotExecutionRecord updated = result.State switch
        {
            BrokerSubmitState.Acknowledged => claimed with
            {
                State = SpotExecutionState.ExitAccepted,
                ExitSubmittedAt = clock.UtcNow,
                ExitBrokerOrderId = result.BrokerOrderId
            },
            // A rejected exit leaves live exposure, so it stays recoverable rather than failing.
            BrokerSubmitState.Rejected => claimed with
            {
                State = SpotExecutionState.ExitDue,
                ExitSubmissionAttemptedAt = null,
                FailureReason = result.ReasonCode ?? "EXIT_REJECTED"
            },
            _ => claimed with { State = SpotExecutionState.ExitSubmitted }
        };
        store.Update(updated);
        return updated;
    }

    private async Task<SpotExecutionRecord> TrackExitAsync(
        SpotExecutionRecord record, CancellationToken cancellationToken)
    {
        BrokerOrderSnapshot? order =
            await broker.FindByClientOrderIdAsync(record.ExitClientOrderId, cancellationToken);
        if (order is null) return record;

        SpotExecutionRecord updated = record with
        {
            ExitBrokerOrderId = order.BrokerOrderId,
            ExitFilledQuantity = order.FilledQuantity,
            ExitAverageFillPrice = order.AverageFillPrice
        };

        updated = order.Status.ToLowerInvariant() switch
        {
            "filled" => updated with
            {
                State = SpotExecutionState.ExitFilled,
                ExitFinalFillAt = clock.UtcNow
            },
            "partially_filled" => updated with { State = SpotExecutionState.ExitPartiallyFilled },
            // Anything that ends the exit order without flattening must retry, never abandon.
            "canceled" or "expired" or "rejected" => updated with
            {
                State = updated.InternalOpenQuantity > 0
                    ? SpotExecutionState.ExitDue
                    : SpotExecutionState.ExitFilled,
                ExitSubmissionAttemptedAt = updated.InternalOpenQuantity > 0 ? null : record.ExitSubmissionAttemptedAt
            },
            _ => updated with { State = SpotExecutionState.ExitAccepted }
        };

        store.Update(updated);
        return updated;
    }

    private async Task<SpotExecutionRecord> ReconcileAsync(
        SpotExecutionRecord record, CancellationToken cancellationToken)
    {
        IReadOnlyList<BrokerPositionSnapshot> positions = await broker.ListPositionsAsync(cancellationToken);
        decimal brokerQuantity = positions
            .Where(position => string.Equals(position.Symbol, record.Symbol, StringComparison.OrdinalIgnoreCase))
            .Sum(position => position.Quantity);

        // Complete only when the broker and the application agree there is nothing left.
        if (brokerQuantity != 0m || record.InternalOpenQuantity != 0m)
        {
            SpotExecutionRecord stillOpen = record with
            {
                State = record.InternalOpenQuantity > 0
                    ? SpotExecutionState.ExitDue
                    : SpotExecutionState.Reconciling,
                ExitSubmissionAttemptedAt = record.InternalOpenQuantity > 0 ? null : record.ExitSubmissionAttemptedAt,
                FailureReason = brokerQuantity != 0m && record.InternalOpenQuantity == 0m
                    ? "BROKER_POSITION_WITHOUT_INTERNAL_EXPOSURE"
                    : record.FailureReason
            };
            store.Update(stillOpen);
            return stillOpen;
        }

        SpotExecutionRecord complete = record with
        {
            State = SpotExecutionState.Complete,
            ReconciledAt = clock.UtcNow,
            CompletedAt = clock.UtcNow
        };
        store.Update(complete);
        return complete;
    }

    /// <summary>
    /// Resolves an ambiguous submission by looking the order up by the ID it would have used.
    /// Generating a replacement ID here would risk a duplicate position, which is the one outcome
    /// worse than stalling.
    /// </summary>
    private async Task<SpotExecutionRecord> RecoverByClientOrderIdAsync(
        SpotExecutionRecord record, string clientOrderId, bool entry, CancellationToken cancellationToken)
    {
        BrokerOrderSnapshot? existing =
            await broker.FindByClientOrderIdAsync(clientOrderId, cancellationToken);
        if (existing is null)
        {
            // Nothing at the broker: the POST never landed, so the claim can be released and the
            // opportunity retried on the next pass.
            SpotExecutionRecord released = entry
                ? record with { State = SpotExecutionState.EntryReserved, EntrySubmissionAttemptedAt = null }
                : record with { State = SpotExecutionState.ExitDue, ExitSubmissionAttemptedAt = null };
            store.Update(released);
            return released;
        }

        SpotExecutionRecord recovered = entry
            ? record with
            {
                State = SpotExecutionState.EntryAccepted,
                EntryBrokerOrderId = existing.BrokerOrderId,
                EntryFilledQuantity = existing.FilledQuantity,
                EntryAverageFillPrice = existing.AverageFillPrice
            }
            : record with
            {
                State = SpotExecutionState.ExitAccepted,
                ExitBrokerOrderId = existing.BrokerOrderId,
                ExitFilledQuantity = existing.FilledQuantity,
                ExitAverageFillPrice = existing.AverageFillPrice
            };
        store.Update(recovered);
        return recovered;
    }
}
