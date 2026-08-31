using System.Security.Cryptography;
using System.Text;
using System.Diagnostics.Metrics;
using QuantDesk.Domain.Execution;
using QuantDesk.Domain.Trading;
using QuantDesk.Runtime.Persistence;
using QuantDesk.Runtime.Time;

namespace QuantDesk.Runtime.Execution;

/// <summary>Owns idempotent PAPER MLeg submission, tracking, exit, restart recovery, and flat reconciliation.</summary>
public sealed class MultiLegExecutionLifecycle(
    IMultiLegBrokerExecutionGateway multiLegBroker,
    IBrokerExecutionGateway broker,
    MultiLegExecutionStore store,
    IRuntimeClock clock,
    TimeSpan brokerSubmitTimeout)
{
    private static readonly Meter Meter = new("QuantDesk.MultiLegExecution", "1.0");
    private static readonly Counter<long> StateTransitions = Meter.CreateCounter<long>(
        "quantdesk_mleg_state_transitions", unit: "transitions",
        description: "Durable multi-leg lifecycle transitions, without broker payloads or credentials.");
    public sealed record EmergencyFlattenResult(bool Complete, bool Pending, string Reason);

    public bool TryReserve(
        string executionId,
        string strategyId,
        int quantity,
        decimal entryLimitPrice,
        decimal exitLimitPrice,
        decimal definedMaximumLoss,
        TimeSpan maximumHoldingPeriod,
        IReadOnlyList<MultiLegExecutionLeg> entryLegs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);
        if (!broker.IsPaperEnvironment || !multiLegBroker.IsPaperEnvironment || !store.IsAvailable()) return false;
        if (definedMaximumLoss <= 0 || maximumHoldingPeriod <= TimeSpan.Zero || brokerSubmitTimeout <= TimeSpan.Zero)
            return false;

        string entryId = DeterministicClientOrderId(executionId, "entry");
        string exitId = DeterministicClientOrderId(executionId, "exit");
        var entry = new MultiLegExecutionCommand(entryId, quantity, ExecutionOrderType.Limit,
            ExecutionTimeInForce.Day, entryLimitPrice, entryLegs);
        var exit = new MultiLegExecutionCommand(exitId, quantity, ExecutionOrderType.Limit,
            ExecutionTimeInForce.Day, exitLimitPrice, entryLegs.Select(CloseLeg).ToArray());
        if (!entry.IsValid() || !exit.IsValid()) return false;

        DateTimeOffset now = clock.UtcNow;
        return store.TryCreate(new MultiLegExecutionRecord(
            executionId, strategyId, MultiLegExecutionState.EntryReserved,
            entry, exit, now, now)
        {
            DefinedMaximumLoss = definedMaximumLoss,
            MaximumHoldingPeriod = maximumHoldingPeriod
        });
    }

    public async Task<MultiLegExecutionRecord> AdvanceAsync(
        string executionId,
        CancellationToken cancellationToken)
    {
        MultiLegExecutionRecord record = store.Find(executionId) ??
            throw new KeyNotFoundException($"MLeg execution '{executionId}' was not found.");
        if (!broker.IsPaperEnvironment || !multiLegBroker.IsPaperEnvironment)
            return Fail(record, MultiLegExecutionState.ReconciliationFailed, "PAPER_VERIFICATION_FAILED");

        switch (record.State)
        {
            case MultiLegExecutionState.EntryReserved:
                await SubmitOrRecoverAsync(record, entry: true, record.MaximumHoldingPeriod, cancellationToken);
                break;
            case MultiLegExecutionState.EntrySubmitted:
            case MultiLegExecutionState.EntryAccepted:
            case MultiLegExecutionState.EntryPartiallyFilled:
                await TrackAsync(record, entry: true, record.MaximumHoldingPeriod, cancellationToken);
                break;
            case MultiLegExecutionState.EntryFilled:
                store.Update(executionId, item => item with { State = MultiLegExecutionState.Holding });
                break;
            case MultiLegExecutionState.Holding:
                if (record.ScheduledExitAt is not null && clock.UtcNow >= record.ScheduledExitAt)
                    store.Update(executionId, item => item with { State = MultiLegExecutionState.ExitDue });
                break;
            case MultiLegExecutionState.ExitDue:
                store.TryReserveExit(executionId, clock.UtcNow);
                break;
            case MultiLegExecutionState.ExitReserved:
                await SubmitOrRecoverAsync(record, entry: false, record.MaximumHoldingPeriod, cancellationToken);
                break;
            case MultiLegExecutionState.ExitSubmitted:
            case MultiLegExecutionState.ExitAccepted:
            case MultiLegExecutionState.ExitPartiallyFilled:
                await TrackAsync(record, entry: false, record.MaximumHoldingPeriod, cancellationToken);
                break;
            case MultiLegExecutionState.ExitFilled:
                store.Update(executionId, item => item with { State = MultiLegExecutionState.Reconciling });
                break;
            case MultiLegExecutionState.Reconciling:
                await ReconcileAsync(record, cancellationToken);
                break;
            case MultiLegExecutionState.SubmissionUnknown:
                await RecoverUnknownAsync(record, record.MaximumHoldingPeriod, cancellationToken);
                break;
            case MultiLegExecutionState.EmergencyFlattening:
                await EmergencyFlattenAsync(record.ExecutionId, cancellationToken);
                break;
        }
        MultiLegExecutionRecord advanced = store.Find(executionId)!;
        if (advanced.State != record.State)
            StateTransitions.Add(1,
                new KeyValuePair<string, object?>("from_state", record.State.ToString()),
                new KeyValuePair<string, object?>("to_state", advanced.State.ToString()),
                new KeyValuePair<string, object?>("terminal", IsTerminal(advanced.State)));
        return advanced;
    }

    public async Task RecoverAllAsync(CancellationToken cancellationToken)
    {
        foreach (MultiLegExecutionRecord record in store.ListNonterminal())
            await AdvanceAsync(record.ExecutionId, cancellationToken);
    }

    public static string DeterministicClientOrderId(string executionId, string leg)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{executionId}:{leg}"));
        return $"qd-opt-{Convert.ToHexString(hash)[..24].ToLowerInvariant()}-{leg}";
    }

    /// <summary>
    /// Flattens only broker-truth exposure owned by this execution. Every leg close has a stable
    /// client identity and is looked up before POST, so a restart or lost response cannot submit
    /// a second closing order. Unknown parent submission state is intentionally left for bounded
    /// recovery; declaring it flat would be unsafe.
    /// </summary>
    public async Task<EmergencyFlattenResult> EmergencyFlattenAsync(
        string executionId,
        CancellationToken cancellationToken)
    {
        MultiLegExecutionRecord record = store.Find(executionId) ??
            throw new KeyNotFoundException($"MLeg execution '{executionId}' was not found.");
        if (!broker.IsPaperEnvironment || !multiLegBroker.IsPaperEnvironment || !store.IsAvailable())
            return new EmergencyFlattenResult(false, false, "PAPER_OR_STORE_UNAVAILABLE");
        if (record.State == MultiLegExecutionState.SubmissionUnknown)
            return new EmergencyFlattenResult(false, true, "SUBMISSION_LOOKUP_REQUIRED");
        if (record.State == MultiLegExecutionState.EmergencyFlattened)
            return new EmergencyFlattenResult(true, false, "ALREADY_FLAT");

        store.Update(executionId, item => item with { State = MultiLegExecutionState.EmergencyFlattening });
        HashSet<string> ownedClientIds = [record.EntryCommand.ClientOrderId, record.ExitCommand.ClientOrderId];
        IReadOnlyList<BrokerOrderSnapshot> openOrders = await broker.ListOpenOrdersAsync(cancellationToken);
        foreach (BrokerOrderSnapshot order in openOrders.Where(order => ownedClientIds.Contains(order.ClientOrderId)))
        {
            BrokerSubmitResult cancelled = await broker.CancelAsync(order.BrokerOrderId, cancellationToken);
            if (cancelled.State != BrokerSubmitState.Acknowledged)
                return HaltEmergency(executionId, "EMERGENCY_CANCEL_REJECTED");
        }
        openOrders = await broker.ListOpenOrdersAsync(cancellationToken);
        if (openOrders.Any(order => ownedClientIds.Contains(order.ClientOrderId)))
            return new EmergencyFlattenResult(false, true, "EMERGENCY_CANCEL_PENDING");

        HashSet<string> ownedSymbols = record.EntryCommand.Legs.Select(leg => leg.Symbol)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<BrokerPositionSnapshot> positions = await broker.ListPositionsAsync(cancellationToken);
        BrokerPositionSnapshot[] exposure = positions.Where(position =>
            ownedSymbols.Contains(position.Symbol) && position.Quantity != 0).ToArray();
        if (exposure.Length == 0)
        {
            store.Update(executionId, item => item with
            {
                State = MultiLegExecutionState.EmergencyFlattened,
                ReconciledAt = clock.UtcNow,
                CompletedAt = clock.UtcNow,
                FailureReason = "EMERGENCY_FLATTEN_VERIFIED"
            });
            return new EmergencyFlattenResult(true, false, "EMERGENCY_FLATTEN_VERIFIED");
        }

        foreach (BrokerPositionSnapshot position in exposure)
        {
            string clientOrderId = DeterministicClientOrderId(executionId, $"flatten:{position.Symbol}");
            BrokerOrderSnapshot? prior = await broker.FindByClientOrderIdAsync(clientOrderId, cancellationToken);
            if (prior is not null)
            {
                if (prior.Status.Equals("rejected", StringComparison.OrdinalIgnoreCase) ||
                    prior.Status.Equals("canceled", StringComparison.OrdinalIgnoreCase))
                    return HaltEmergency(executionId, "EMERGENCY_CLOSE_TERMINAL_FAILURE");
                continue;
            }
            long now = clock.UtcNow.ToUnixTimeMilliseconds();
            var close = new ExecutionCommand(now, ExecutionPriority.EmergencyExit, 0, 0, clientOrderId,
                position.InstrumentSlot, position.Quantity > 0 ? OrderSide.Sell : OrderSide.Buy,
                position.Quantity > 0 ? PositionIntent.SellToClose : PositionIntent.BuyToClose,
                ExecutionOrderType.Market, ExecutionTimeInForce.Day, Math.Abs(position.Quantity), null,
                now, now + (long)brokerSubmitTimeout.TotalMilliseconds, record.StrategyId);
            BrokerSubmitResult submitted = await broker.SubmitAsync(close, cancellationToken);
            if (submitted.State == BrokerSubmitState.Rejected)
                return HaltEmergency(executionId, "EMERGENCY_CLOSE_REJECTED");
            if (submitted.State == BrokerSubmitState.Unknown)
                return new EmergencyFlattenResult(false, true, "EMERGENCY_CLOSE_LOOKUP_REQUIRED");
        }
        return new EmergencyFlattenResult(false, true, "EMERGENCY_CLOSES_SUBMITTED");
    }

    private EmergencyFlattenResult HaltEmergency(string executionId, string reason)
    {
        store.Update(executionId, item => item with
        {
            State = MultiLegExecutionState.ReconciliationFailed,
            FailureReason = reason
        });
        return new EmergencyFlattenResult(false, false, reason);
    }

    private async Task SubmitOrRecoverAsync(
        MultiLegExecutionRecord record,
        bool entry,
        TimeSpan maximumHoldingPeriod,
        CancellationToken cancellationToken)
    {
        MultiLegExecutionCommand command = entry ? record.EntryCommand : record.ExitCommand;
        BrokerOrderSnapshot? existing = await multiLegBroker.FindByClientOrderIdAsync(
            command.ClientOrderId, cancellationToken);
        if (existing is not null)
        {
            ApplyOrder(record.ExecutionId, existing, entry, maximumHoldingPeriod);
            return;
        }

        bool claimed = entry
            ? store.TryClaimEntrySubmission(record.ExecutionId, clock.UtcNow)
            : store.TryClaimExitSubmission(record.ExecutionId, clock.UtcNow);
        if (!claimed) return;

        try
        {
            using var submitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            submitCancellation.CancelAfter(brokerSubmitTimeout);
            BrokerSubmitResult result = await multiLegBroker.SubmitMultiLegAsync(
                command, submitCancellation.Token);
            if (result.State == BrokerSubmitState.Unknown)
                await RecoverAmbiguousAsync(
                    record.ExecutionId, command.ClientOrderId, entry, cancellationToken);
            else
                ApplySubmission(record.ExecutionId, result, entry);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await RecoverAmbiguousAsync(record.ExecutionId, command.ClientOrderId, entry, cancellationToken);
        }
        catch (HttpRequestException)
        {
            await RecoverAmbiguousAsync(record.ExecutionId, command.ClientOrderId, entry, cancellationToken);
        }
    }

    private async Task RecoverUnknownAsync(
        MultiLegExecutionRecord record,
        TimeSpan maximumHoldingPeriod,
        CancellationToken cancellationToken)
    {
        bool entry = record.ExitSubmissionAttemptedAt is null;
        string clientOrderId = entry
            ? record.EntryCommand.ClientOrderId
            : record.ExitCommand.ClientOrderId;
        BrokerOrderSnapshot? recovered = await multiLegBroker.FindByClientOrderIdAsync(
            clientOrderId, cancellationToken);
        if (recovered is not null)
        {
            ApplyOrder(record.ExecutionId, recovered, entry, maximumHoldingPeriod);
            return;
        }

        DateTimeOffset? attemptedAt = entry
            ? record.EntrySubmissionAttemptedAt
            : record.ExitSubmissionAttemptedAt;
        if (attemptedAt is not null && clock.UtcNow - attemptedAt >= brokerSubmitTimeout)
        {
            // A retry with a new identifier could duplicate a broker order that is only delayed
            // in becoming queryable. Preserve the original identity and halt for intervention.
            store.Update(record.ExecutionId, item => item with
            {
                State = MultiLegExecutionState.SubmissionUnresolved,
                FailureReason = entry
                    ? "ENTRY_SUBMISSION_LOOKUP_TIMEOUT"
                    : "EXIT_SUBMISSION_LOOKUP_TIMEOUT"
            });
        }
    }

    private async Task RecoverAmbiguousAsync(
        string executionId,
        string clientOrderId,
        bool entry,
        CancellationToken cancellationToken)
    {
        BrokerOrderSnapshot? recovered = await multiLegBroker.FindByClientOrderIdAsync(clientOrderId, cancellationToken);
        if (recovered is not null)
        {
            ApplyOrder(executionId, recovered, entry, TimeSpan.Zero);
            return;
        }
        store.Update(executionId, item => item with
        {
            State = MultiLegExecutionState.SubmissionUnknown,
            FailureReason = entry ? "ENTRY_SUBMISSION_UNKNOWN" : "EXIT_SUBMISSION_UNKNOWN"
        });
    }

    private async Task TrackAsync(
        MultiLegExecutionRecord record,
        bool entry,
        TimeSpan maximumHoldingPeriod,
        CancellationToken cancellationToken)
    {
        string clientOrderId = entry
            ? record.EntryCommand.ClientOrderId
            : record.ExitCommand.ClientOrderId;
        BrokerOrderSnapshot? order = await multiLegBroker.FindByClientOrderIdAsync(
            clientOrderId, cancellationToken);
        if (order is null) return;
        DateTimeOffset? acknowledgedAt = entry ? record.EntryAcknowledgedAt : record.ExitAcknowledgedAt;
        if (IsStaleUnfilled(order, acknowledgedAt))
        {
            BrokerSubmitResult cancelled = await broker.CancelAsync(order.BrokerOrderId, cancellationToken);
            if (cancelled.State == BrokerSubmitState.Acknowledged)
            {
                store.Update(record.ExecutionId, item => item with
                {
                    State = entry ? MultiLegExecutionState.EntryRejected : MultiLegExecutionState.ExitRejected,
                    FailureReason = entry ? "ENTRY_FILL_TIMEOUT_CANCELED" : "EXIT_FILL_TIMEOUT_CANCELED"
                });
                return;
            }
        }
        ApplyOrder(record.ExecutionId, order, entry, maximumHoldingPeriod);
    }

    private bool IsStaleUnfilled(BrokerOrderSnapshot order, DateTimeOffset? acknowledgedAt) =>
        acknowledgedAt is not null && clock.UtcNow - acknowledgedAt >= brokerSubmitTimeout &&
        order.FilledQuantity == 0 && order.Status is "accepted" or "new" or "pending_new";

    private void ApplySubmission(string executionId, BrokerSubmitResult result, bool entry)
    {
        store.Update(executionId, item => result.State switch
        {
            BrokerSubmitState.Acknowledged when !string.IsNullOrWhiteSpace(result.BrokerOrderId) => item with
            {
                State = entry ? MultiLegExecutionState.EntrySubmitted : MultiLegExecutionState.ExitSubmitted,
                EntryBrokerOrderId = entry ? result.BrokerOrderId : item.EntryBrokerOrderId,
                ExitBrokerOrderId = entry ? item.ExitBrokerOrderId : result.BrokerOrderId,
                EntrySubmittedAt = entry ? clock.UtcNow : item.EntrySubmittedAt,
                ExitSubmittedAt = entry ? item.ExitSubmittedAt : clock.UtcNow,
                FailureReason = null
            },
            BrokerSubmitState.Rejected => item with
            {
                State = entry ? MultiLegExecutionState.EntryRejected : MultiLegExecutionState.ExitRejected,
                FailureReason = result.ReasonCode ?? (entry ? "ENTRY_REJECTED" : "EXIT_REJECTED")
            },
            _ => item with
            {
                State = MultiLegExecutionState.SubmissionUnknown,
                FailureReason = result.ReasonCode ?? "BROKER_SUBMISSION_UNKNOWN"
            }
        });
    }

    private void ApplyOrder(
        string executionId,
        BrokerOrderSnapshot order,
        bool entry,
        TimeSpan maximumHoldingPeriod)
    {
        MultiLegExecutionRecord tracked = store.Find(executionId) ??
            throw new KeyNotFoundException($"MLeg execution '{executionId}' was not found.");
        if (!HasExpectedLegFills(order, entry ? tracked.EntryCommand : tracked.ExitCommand))
        {
            store.Update(executionId, item => item with
            {
                State = MultiLegExecutionState.ReconciliationFailed,
                FailureReason = entry ? "ENTRY_BROKEN_LEG_FILL_RATIO" : "EXIT_BROKEN_LEG_FILL_RATIO"
            });
            return;
        }
        string status = order.Status.Trim().ToLowerInvariant();
        store.Update(executionId, item => status switch
        {
            "accepted" or "new" or "pending_new" => ApplyAccepted(item, order, entry),
            "partially_filled" => ApplyPartial(item, order, entry),
            "filled" => ApplyFilled(item, order, entry, maximumHoldingPeriod),
            "rejected" or "canceled" or "expired" => item with
            {
                State = entry ? MultiLegExecutionState.EntryRejected : MultiLegExecutionState.ExitRejected,
                FailureReason = $"{(entry ? "ENTRY" : "EXIT")}_{status.ToUpperInvariant()}"
            },
            _ => item
        });
    }

    private MultiLegExecutionRecord ApplyAccepted(
        MultiLegExecutionRecord item,
        BrokerOrderSnapshot order,
        bool entry) => item with
    {
        State = entry ? MultiLegExecutionState.EntryAccepted : MultiLegExecutionState.ExitAccepted,
        EntryBrokerOrderId = entry ? order.BrokerOrderId : item.EntryBrokerOrderId,
        ExitBrokerOrderId = entry ? item.ExitBrokerOrderId : order.BrokerOrderId,
        EntryAcknowledgedAt = entry ? order.SubmittedAt ?? clock.UtcNow : item.EntryAcknowledgedAt,
        ExitAcknowledgedAt = entry ? item.ExitAcknowledgedAt : order.SubmittedAt ?? clock.UtcNow,
        FailureReason = null
    };

    private static MultiLegExecutionRecord ApplyPartial(
        MultiLegExecutionRecord item,
        BrokerOrderSnapshot order,
        bool entry) => item with
    {
        State = entry ? MultiLegExecutionState.EntryPartiallyFilled : MultiLegExecutionState.ExitPartiallyFilled,
        EntryBrokerOrderId = entry ? order.BrokerOrderId : item.EntryBrokerOrderId,
        ExitBrokerOrderId = entry ? item.ExitBrokerOrderId : order.BrokerOrderId,
        EntryFilledQuantity = entry ? order.FilledQuantity : item.EntryFilledQuantity,
        ExitFilledQuantity = entry ? item.ExitFilledQuantity : order.FilledQuantity,
        EntryAverageFillPrice = entry ? order.AverageFillPrice : item.EntryAverageFillPrice,
        ExitAverageFillPrice = entry ? item.ExitAverageFillPrice : order.AverageFillPrice,
        FailureReason = null
    };

    private MultiLegExecutionRecord ApplyFilled(
        MultiLegExecutionRecord item,
        BrokerOrderSnapshot order,
        bool entry,
        TimeSpan maximumHoldingPeriod)
    {
        DateTimeOffset filledAt = order.FilledAt ?? order.UpdatedAt ?? clock.UtcNow;
        return item with
        {
            State = entry ? MultiLegExecutionState.EntryFilled : MultiLegExecutionState.ExitFilled,
            EntryBrokerOrderId = entry ? order.BrokerOrderId : item.EntryBrokerOrderId,
            ExitBrokerOrderId = entry ? item.ExitBrokerOrderId : order.BrokerOrderId,
            EntryFilledQuantity = entry ? order.FilledQuantity : item.EntryFilledQuantity,
            ExitFilledQuantity = entry ? item.ExitFilledQuantity : order.FilledQuantity,
            EntryAverageFillPrice = entry ? order.AverageFillPrice : item.EntryAverageFillPrice,
            ExitAverageFillPrice = entry ? item.ExitAverageFillPrice : order.AverageFillPrice,
            EntryFinalFillAt = entry ? filledAt : item.EntryFinalFillAt,
            EntryLegs = entry ? order.Legs : item.EntryLegs,
            ExitFinalFillAt = entry ? item.ExitFinalFillAt : filledAt,
            ExitLegs = entry ? item.ExitLegs : order.Legs,
            HoldStartedAt = entry ? filledAt : item.HoldStartedAt,
            ScheduledExitAt = entry ? filledAt.Add(maximumHoldingPeriod) : item.ScheduledExitAt,
            FailureReason = null
        };
    }

    private static bool HasExpectedLegFills(BrokerOrderSnapshot order, MultiLegExecutionCommand command)
    {
        // Some broker order views omit nested legs before any fill. Once legs are present, each
        // reported leg must map exactly to a requested OCC symbol and preserve its ratio. A
        // final fill without nested legs cannot establish individual contract ownership, so it
        // is not permitted to enter the reconciliation path.
        if (order.Legs.Count == 0)
            return !string.Equals(order.Status, "filled", StringComparison.OrdinalIgnoreCase);
        if (order.Legs.Count != command.Legs.Count) return false;
        foreach (MultiLegExecutionLeg expected in command.Legs)
        {
            BrokerOrderLegSnapshot? actual = order.Legs.SingleOrDefault(leg =>
                string.Equals(leg.Symbol, expected.Symbol, StringComparison.OrdinalIgnoreCase));
            if (actual is null || actual.FilledQuantity < 0 ||
                actual.FilledQuantity > order.FilledQuantity * expected.RatioQuantity)
                return false;
        }
        return true;
    }

    private async Task ReconcileAsync(MultiLegExecutionRecord record, CancellationToken cancellationToken)
    {
        IReadOnlyList<BrokerOrderSnapshot> openOrders = await broker.ListOpenOrdersAsync(cancellationToken);
        IReadOnlyList<BrokerPositionSnapshot> positions = await broker.ListPositionsAsync(cancellationToken);
        HashSet<string> ownedClientIds =
        [
            record.EntryCommand.ClientOrderId,
            record.ExitCommand.ClientOrderId
        ];
        HashSet<string> ownedSymbols = record.EntryCommand.Legs
            .Select(leg => leg.Symbol)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool unresolvedOrder = openOrders.Any(order => ownedClientIds.Contains(order.ClientOrderId));
        bool internalLegsKnown = record.TryGetInternalOpenLegQuantities(out IReadOnlyDictionary<string, decimal> internalLegs);
        bool brokerLegMismatch = !internalLegsKnown || ownedSymbols.Any(symbol =>
        {
            decimal brokerQuantity = positions.Where(position =>
                    string.Equals(position.Symbol, symbol, StringComparison.OrdinalIgnoreCase))
                .Sum(position => position.Quantity);
            return !internalLegsKnown || brokerQuantity != internalLegs.GetValueOrDefault(symbol);
        });
        bool internalExposure = record.InternalOpenQuantity != 0 ||
            (internalLegsKnown && internalLegs.Values.Any(quantity => quantity != 0));
        store.Update(record.ExecutionId, item => unresolvedOrder || brokerLegMismatch || internalExposure
            ? item with
            {
                State = MultiLegExecutionState.ReconciliationFailed,
                ReconciledAt = clock.UtcNow,
                FailureReason = $"RECONCILIATION_MISMATCH:orders={unresolvedOrder};legs={brokerLegMismatch};internal={internalExposure}"
            }
            : item with
            {
                State = MultiLegExecutionState.Complete,
                ReconciledAt = clock.UtcNow,
                CompletedAt = clock.UtcNow,
                FailureReason = null
            });
    }

    private MultiLegExecutionRecord Fail(
        MultiLegExecutionRecord record,
        MultiLegExecutionState state,
        string reason)
    {
        store.Update(record.ExecutionId, item => item with { State = state, FailureReason = reason });
        return store.Find(record.ExecutionId)!;
    }

    private static MultiLegExecutionLeg CloseLeg(MultiLegExecutionLeg leg) => leg with
    {
        Side = leg.Side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy,
        PositionIntent = leg.PositionIntent switch
        {
            PositionIntent.BuyToOpen => PositionIntent.SellToClose,
            PositionIntent.SellToOpen => PositionIntent.BuyToClose,
            _ => throw new ArgumentException("Entry MLeg must use opening position intents.")
        }
    };

    private static bool IsTerminal(MultiLegExecutionState state) => state is
        MultiLegExecutionState.Complete or MultiLegExecutionState.EntryRejected or
        MultiLegExecutionState.ExitRejected or MultiLegExecutionState.ReconciliationFailed or
        MultiLegExecutionState.SubmissionUnresolved or MultiLegExecutionState.EmergencyFlattened;
}
