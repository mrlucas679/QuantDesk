using QuantDesk.Domain.Execution;
using QuantDesk.Domain.Runtime;
using QuantDesk.Runtime.Modes;
using QuantDesk.Runtime.Reservations;

namespace QuantDesk.Runtime.Execution;

public sealed class ExecutionWorker(
    IBrokerExecutionGateway broker,
    ReservationLedger reservations,
    RuntimeModeState runtimeMode,
    TimeSpan brokerSubmitTimeout)
{
    public void ApplyTradeUpdate(ExecutionIntent intent, BrokerTradeUpdate update)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (string.IsNullOrWhiteSpace(update.ClientOrderId))
            throw new ArgumentException("Broker update is missing client order identity.", nameof(update));
        switch (update.Kind)
        {
            case BrokerTradeUpdateKind.PartialFill:
                if (intent.State is ExecutionIntentState.Acknowledged or ExecutionIntentState.Reconciling)
                    intent.TransitionTo(ExecutionIntentState.PartiallyFilled);
                return;
            case BrokerTradeUpdateKind.Fill:
                if (intent.State is ExecutionIntentState.Acknowledged or ExecutionIntentState.PartiallyFilled or ExecutionIntentState.Reconciling)
                    intent.TransitionTo(ExecutionIntentState.Filled);
                return;
            case BrokerTradeUpdateKind.Canceled:
            case BrokerTradeUpdateKind.Expired:
            case BrokerTradeUpdateKind.Rejected:
                if (intent.State is ExecutionIntentState.Submitted or ExecutionIntentState.Reconciling)
                    intent.TransitionTo(ExecutionIntentState.Failed);
                else if (intent.State is not (ExecutionIntentState.Completed or ExecutionIntentState.Failed or ExecutionIntentState.Canceled))
                    intent.TransitionTo(ExecutionIntentState.Canceled);
                return;
            case BrokerTradeUpdateKind.New:
            case BrokerTradeUpdateKind.Unknown:
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(update));
        }
    }

    public async Task<BrokerSubmitResult> SubmitOneAsync(
        ExecutionIntent intent,
        ExecutionCommand command,
        long nowMonotonicTicks,
        CancellationToken stoppingToken)
    {
        ValidateExecutionFence(intent, command, nowMonotonicTicks);
        intent.TransitionTo(ExecutionIntentState.Submitted);

        using var submitCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        submitCancellation.CancelAfter(brokerSubmitTimeout);

        try
        {
            BrokerSubmitResult result = await broker.SubmitAsync(command, submitCancellation.Token);
            ApplyBrokerResult(intent, command, result);
            return result;
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            reservations.MarkUnknown(command.RiskReservationId);
            intent.TransitionTo(ExecutionIntentState.Reconciling);
            return new BrokerSubmitResult(BrokerSubmitState.Unknown, null, "BROKER_SUBMIT_TIMEOUT", null);
        }
    }

    private void ValidateExecutionFence(
        ExecutionIntent intent,
        ExecutionCommand command,
        long nowMonotonicTicks)
    {
        if (intent.State != ExecutionIntentState.Queued)
        {
            throw new InvalidOperationException("Only a queued execution intent may be submitted.");
        }

        if (nowMonotonicTicks > command.ExpiresMonotonicTicks)
        {
            throw new InvalidOperationException("Execution command expired before submission.");
        }

        if (command.RiskReservationId != command.CapitalReservationId)
        {
            throw new InvalidOperationException("The current atomic ledger requires one combined risk and capital reservation.");
        }

        if (!reservations.IsActive(command.RiskReservationId))
        {
            throw new InvalidOperationException("Execution requires an active reservation.");
        }

        SystemMode mode = runtimeMode.Snapshot().Mode;
        bool entry = command.Priority is ExecutionPriority.ExploitationEntry or ExecutionPriority.ExplorationEntry;
        if (entry && mode != SystemMode.Ready)
        {
            throw new InvalidOperationException("New entries are allowed only while the runtime is ready.");
        }

        if (!entry && mode is SystemMode.Booting or SystemMode.Preflight or SystemMode.Warming or SystemMode.Syncing or SystemMode.Shutdown)
        {
            throw new InvalidOperationException("Risk reduction is unavailable in the current runtime mode.");
        }
    }

    private void ApplyBrokerResult(
        ExecutionIntent intent,
        ExecutionCommand command,
        BrokerSubmitResult result)
    {
        switch (result.State)
        {
            case BrokerSubmitState.Acknowledged:
                if (string.IsNullOrWhiteSpace(result.BrokerOrderId))
                {
                    reservations.MarkUnknown(command.RiskReservationId);
                    intent.TransitionTo(ExecutionIntentState.Reconciling);
                    return;
                }

                intent.AttachBrokerOrderId(result.BrokerOrderId);
                intent.TransitionTo(ExecutionIntentState.Acknowledged);
                return;
            case BrokerSubmitState.Rejected:
                reservations.Release(command.RiskReservationId);
                intent.TransitionTo(ExecutionIntentState.Failed);
                return;
            case BrokerSubmitState.Unknown:
                reservations.MarkUnknown(command.RiskReservationId);
                intent.TransitionTo(ExecutionIntentState.Reconciling);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(result), "Unsupported broker submission result.");
        }
    }
}
