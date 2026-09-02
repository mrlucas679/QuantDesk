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
    IHoldInterrupt? holdInterrupt = null,
    IHeldPositionMarker? referencePrices = null)
{
    /// <summary>
    /// The most of an entry the venue may take as an in-kind fee.
    ///
    /// Alpaca's spot crypto taker fee is 0.25%; half a percent leaves room for a schedule change
    /// without being loose enough to absorb a real position. The diagnostic lane uses the same
    /// bound for the same reason, and the two must not drift apart -- a residual written off here
    /// and flagged there would make the two lanes disagree about whether an account is flat.
    /// </summary>
    private const decimal MaximumInKindFeeShare = 0.005m;


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
        PositionOwnership? ownership = null,
        decimal? entryReferencePrice = null,
        decimal? accountEquityBefore = null)
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
            Ownership = ownership,
            EntryReferencePrice = entryReferencePrice,
            AccountEquityBefore = accountEquityBefore
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

    /// <summary>Projects a spot record onto the view every early-exit rule reads.</summary>
    private static HeldPosition HeldPositionView(SpotExecutionRecord record) => new(
        record.ExecutionId,
        record.Symbol,
        record.EntryFilledQuantity,
        record.EntryAverageFillPrice,
        record.DefinedMaximumLoss,
        record.Ownership,
        EarliestLegExpiry: null);

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
            : holdInterrupt?.Evaluate(HeldPositionView(record)) ?? HoldInterrupt.None;

        if (!timerDue && !interrupt.ShouldExitNow) return record;

        SpotExecutionRecord updated = record with
        {
            State = SpotExecutionState.ExitDue,
            EarlyExitReason = interrupt.Reason,

            // The exit's decision price, captured here rather than at completion.
            //
            // Implementation shortfall measures the gap between the decision and the outcome, so
            // the price that belongs in it is the mid at the moment the exit was decided -- which
            // is now. Completion is the wrong moment on both counts: it is later than the decision,
            // and it is reached through a broker round trip during which the market state can go
            // cold. On 2026-09-02 two positions reconciled flat seconds after a restart, before
            // their slots had a healthy quote, and completed with no exit reference at all. Four of
            // the day's seven completed round trips could therefore never say what they cost, and
            // nothing about the record showed that the measurement had been lost.
            //
            // Here the hold loop has just refreshed the quote for this symbol, so a mid is
            // available whenever the feed is. Completion keeps its own fallback for a record that
            // never passed through a quoted exit decision.
            ExitReferencePrice = record.ExitReferencePrice ?? referencePrices?.CurrentMid(record.Symbol),
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

        // Sell what is actually held, not what was bought.
        //
        // Alpaca charges its spot crypto fee *in kind*: it takes the fee out of the quantity
        // delivered, so an entry that filled 1.54344806 leaves 1.539589439 in the account. Asking to
        // sell the filled quantity is asking to sell roughly 0.25% more than exists, and the venue
        // refuses -- "insufficient balance for AAVE (requested: 1.54344806, available:
        // 1.539589439)".
        //
        // A rejected exit correctly returns to ExitDue and retries, which turned the error into a
        // permanent one: every retry asked for the same impossible quantity. The position could not
        // be closed by the managed path at all, so the holding period was not actually bounded --
        // and this would have happened to every crypto round trip, not just this one.
        //
        // The broker's own position is the only trustworthy figure here, for the same reason
        // account equity is the only trustworthy cost: the fee is invisible in the fill.
        decimal sellable = await SellableQuantityAsync(record, cancellationToken);
        if (sellable <= 0m)
        {
            // Nothing left to sell. Reconciliation decides whether that is correct.
            SpotExecutionRecord nothingHeld = record with { State = SpotExecutionState.Reconciling };
            store.Update(nothingHeld);
            return nothingHeld;
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
            sellable,
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

    /// <summary>
    /// How much of this instrument can actually be sold right now.
    ///
    /// The lesser of what this execution believes it opened and what the broker says is in the
    /// account. The two differ by the in-kind fee, and the broker's figure is the one the venue
    /// will enforce; the internal figure caps it so a position opened by some other execution is
    /// never sold by this one.
    ///
    /// A failure to read positions returns the internal quantity, preserving the previous
    /// behaviour: a temporary inability to ask is not a reason to stop trying to close.
    /// </summary>
    private async Task<decimal> SellableQuantityAsync(
        SpotExecutionRecord record, CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<BrokerPositionSnapshot> positions =
                await broker.ListPositionsAsync(cancellationToken);
            decimal held = positions
                .Where(position => BrokerSymbol.Matches(position.Symbol, record.Symbol))
                .Sum(position => position.Quantity);

            return held <= 0m ? 0m : Math.Min(record.InternalOpenQuantity, held);
        }
        catch (Exception exception) when (exception is not OperationCanceledException ||
                                          !cancellationToken.IsCancellationRequested)
        {
            return record.InternalOpenQuantity;
        }
    }

    private async Task<SpotExecutionRecord> TrackExitAsync(
        SpotExecutionRecord record, CancellationToken cancellationToken)
    {
        BrokerOrderSnapshot? order =
            await broker.FindByClientOrderIdAsync(record.ExitClientOrderId, cancellationToken);

        // No order under this ID means the submission never reached the venue.
        //
        // Returning the record unchanged left it here permanently: the exit was believed to be in
        // flight, nothing was tracking it because nothing existed, and the position stayed open
        // past its deadline with no retry and no error. AAVE sat in exactly this state for over an
        // hour after its exit was refused for insufficient balance.
        //
        // Sending it back to ExitDue re-runs the submission, which is safe because the exit client
        // order ID is deterministic: if an order does exist after all, the resubmission is
        // recognised as the same order rather than becoming a second one.
        if (order is null)
        {
            SpotExecutionRecord retry = record with
            {
                State = SpotExecutionState.ExitDue,
                ExitSubmissionAttemptedAt = null,
            };
            store.Update(retry);
            return retry;
        }

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
            // The venue spells spot crypto without the separator, so an ordinal comparison here
            // made every crypto position invisible -- to this reconciliation as much as to the exit.
            .Where(position => BrokerSymbol.Matches(position.Symbol, record.Symbol))
            .Sum(position => position.Quantity);

        // The internal ledger keeps a residual the account never held.
        //
        // The venue takes its spot crypto fee in kind, so an entry that filled 1.54344806 delivered
        // 1.539589439. The exit sells what the account holds, correctly, and the ledger is then left
        // believing 0.00385862 is still open -- the fee, recorded as exposure.
        //
        // That residual can never be sold: the broker holds nothing, so the exit finds nothing to
        // sell and hands back to reconciliation, which sees internal exposure and sends it to
        // ExitDue again. AAVE and UNI were both circling that loop, substance sold, unable to
        // finish.
        //
        // A residual within the fee's share of the entry, against a broker position of zero, is the
        // fee and not a position. Treating it as exposure is what keeps the record open; treating a
        // *larger* gap as the fee would be how real exposure gets written off, which is why this is
        // bounded by the same share the diagnostic lane already uses.
        // The ledger is not rewritten -- the fill quantities are what the venue reported and stay
        // that way. What changes is the reading: a residual this small, against a broker position
        // of zero, is the fee rather than exposure.
        bool withinFee = brokerQuantity == 0m && record.EntryFilledQuantity > 0m &&
            record.InternalOpenQuantity <= record.EntryFilledQuantity * MaximumInKindFeeShare;

        // The broker is authoritative about what exists.
        //
        // A position can leave without this system selling it: closed by hand in the venue's own
        // interface, liquidated, or otherwise resolved outside the lifecycle. The existing check
        // catches the opposite case -- a broker position with no internal exposure -- and there was
        // nothing for this one, so the record simply circled: the exit found nothing to sell and
        // handed to reconciliation, reconciliation saw internal exposure and sent it back.
        //
        // Six positions were in exactly that loop after they were closed by hand, and their symbols
        // could never have traded again, because the lane reads its own store to decide whether an
        // instrument is already held.
        //
        // The discrepancy is recorded rather than hidden. Our arithmetic said we held something and
        // the account says otherwise, and that is worth being able to see afterwards even though
        // the account is right.
        bool closedElsewhere = brokerQuantity == 0m && record.InternalOpenQuantity > 0m && !withinFee;

        bool internallyFlat = record.InternalOpenQuantity == 0m || withinFee || closedElsewhere;

        // Complete only when the broker and the application agree there is nothing left.
        if (brokerQuantity != 0m || !internallyFlat)
        {
            SpotExecutionRecord stillOpen = record with
            {
                State = !internallyFlat
                    ? SpotExecutionState.ExitDue
                    : SpotExecutionState.Reconciling,
                ExitSubmissionAttemptedAt = !internallyFlat ? null : record.ExitSubmissionAttemptedAt,
                FailureReason = brokerQuantity != 0m && internallyFlat
                    ? "BROKER_POSITION_WITHOUT_INTERNAL_EXPOSURE"
                    : record.FailureReason
            };
            store.Update(stillOpen);
            return stillOpen;
        }

        // Read once, here, because this is the only moment the account is known to be flat for this
        // execution. Taken earlier it would still carry the position's mark; taken later another
        // execution may have moved it, and the difference would be attributed to this trip.
        BrokerAccountSnapshot? account = await broker.GetAccountAsync(cancellationToken);

        SpotExecutionRecord complete = record with
        {
            State = SpotExecutionState.Complete,
            FailureReason = closedElsewhere
                ? $"CLOSED_OUTSIDE_SYSTEM:{record.InternalOpenQuantity} unaccounted"
                : record.FailureReason,
            ReconciledAt = clock.UtcNow,
            CompletedAt = clock.UtcNow,
            AccountEquityAfter = account?.Equity ?? record.AccountEquityAfter,
            ExitReferencePrice = record.ExitReferencePrice ?? referencePrices?.CurrentMid(record.Symbol)
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
