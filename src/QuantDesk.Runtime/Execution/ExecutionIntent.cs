using QuantDesk.Domain.Execution;

namespace QuantDesk.Runtime.Execution;

public sealed class ExecutionIntent
{
    private static readonly IReadOnlyDictionary<ExecutionIntentState, HashSet<ExecutionIntentState>> AllowedTransitions =
        new Dictionary<ExecutionIntentState, HashSet<ExecutionIntentState>>
        {
            [ExecutionIntentState.Created] = [ExecutionIntentState.Approved, ExecutionIntentState.Canceled, ExecutionIntentState.Failed],
            [ExecutionIntentState.Approved] = [ExecutionIntentState.Reserved, ExecutionIntentState.Canceled, ExecutionIntentState.Failed],
            [ExecutionIntentState.Reserved] = [ExecutionIntentState.Queued, ExecutionIntentState.Canceled, ExecutionIntentState.Failed],
            [ExecutionIntentState.Queued] = [ExecutionIntentState.Submitted, ExecutionIntentState.Canceled, ExecutionIntentState.Failed],
            [ExecutionIntentState.Submitted] = [ExecutionIntentState.Acknowledged, ExecutionIntentState.Reconciling, ExecutionIntentState.Failed],
            [ExecutionIntentState.Acknowledged] = [ExecutionIntentState.PartiallyFilled, ExecutionIntentState.Filled, ExecutionIntentState.Canceled, ExecutionIntentState.Reconciling],
            [ExecutionIntentState.PartiallyFilled] = [ExecutionIntentState.Filled, ExecutionIntentState.Canceled, ExecutionIntentState.Reconciling],
            [ExecutionIntentState.Filled] = [ExecutionIntentState.PositionManaging, ExecutionIntentState.Completed],
            [ExecutionIntentState.PositionManaging] = [ExecutionIntentState.Closing, ExecutionIntentState.Reconciling],
            [ExecutionIntentState.Closing] = [ExecutionIntentState.Completed, ExecutionIntentState.Reconciling, ExecutionIntentState.Failed],
            [ExecutionIntentState.Reconciling] = [ExecutionIntentState.Acknowledged, ExecutionIntentState.PartiallyFilled, ExecutionIntentState.Filled, ExecutionIntentState.Completed, ExecutionIntentState.Failed]
        };

    public ExecutionIntent(long intentId, long candidateId, string strategyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);
        IntentId = intentId;
        CandidateId = candidateId;
        StrategyId = strategyId.Trim();
    }

    public long IntentId { get; }

    public long CandidateId { get; }

    public string StrategyId { get; }

    public ExecutionIntentState State { get; private set; } = ExecutionIntentState.Created;

    public string? ClientOrderId { get; private set; }

    public string? BrokerOrderId { get; private set; }

    public long? RiskReservationId { get; private set; }

    public long? CapitalReservationId { get; private set; }

    public void AttachApproval(string clientOrderId, long riskReservationId, long capitalReservationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientOrderId);
        if (State != ExecutionIntentState.Approved)
        {
            throw new InvalidOperationException("Approval identifiers can only be attached to an approved intent.");
        }

        ClientOrderId = clientOrderId.Trim();
        RiskReservationId = riskReservationId;
        CapitalReservationId = capitalReservationId;
        TransitionTo(ExecutionIntentState.Reserved);
    }

    public void AttachBrokerOrderId(string brokerOrderId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brokerOrderId);
        BrokerOrderId = brokerOrderId.Trim();
    }

    public void TransitionTo(ExecutionIntentState nextState)
    {
        if (!AllowedTransitions.TryGetValue(State, out HashSet<ExecutionIntentState>? allowed) || !allowed.Contains(nextState))
        {
            throw new InvalidOperationException($"Execution intent cannot transition from {State} to {nextState}.");
        }

        State = nextState;
    }
}

