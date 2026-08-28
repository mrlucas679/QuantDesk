using QuantDesk.Domain.Execution;
using QuantDesk.Domain.Runtime;
using QuantDesk.Runtime.Modes;
using BrokerOrderSnapshot = QuantDesk.Domain.Execution.BrokerOrderSnapshot;

namespace QuantDesk.Runtime.Reconciliation;

public sealed record ReconciliationInput(
    IReadOnlySet<string> LocalClientOrderIds,
    IReadOnlyDictionary<int, decimal> LocalPositionQuantities,
    IReadOnlyList<BrokerOrderSnapshot> BrokerOpenOrders,
    IReadOnlyList<BrokerPositionSnapshot> BrokerPositions);

public sealed record ReconciliationResult(
    bool IsReconciled,
    IReadOnlyList<string> MismatchCodes,
    IReadOnlyList<string> ExternalClientOrderIds,
    IReadOnlyList<int> PositionMismatches);

public sealed class ReconciliationService(RuntimeModeState runtimeMode)
{
    public ReconciliationResult Reconcile(ReconciliationInput input)
    {
        var mismatches = new List<string>();
        var externalOrders = input.BrokerOpenOrders
            .Where(order => !input.LocalClientOrderIds.Contains(order.ClientOrderId))
            .Select(order => order.ClientOrderId)
            .ToArray();

        if (externalOrders.Length > 0) mismatches.Add("UNKNOWN_BROKER_ORDER");

        var brokerPositions = input.BrokerPositions.ToDictionary(position => position.InstrumentSlot, position => position.Quantity);
        int[] positionMismatches = input.LocalPositionQuantities
            .Where(local => !brokerPositions.TryGetValue(local.Key, out decimal brokerQuantity) || brokerQuantity != local.Value)
            .Select(local => local.Key)
            .Concat(brokerPositions.Keys.Where(slot => !input.LocalPositionQuantities.ContainsKey(slot)))
            .Distinct()
            .ToArray();

        if (positionMismatches.Length > 0) mismatches.Add("POSITION_MISMATCH");

        bool reconciled = mismatches.Count == 0;
        runtimeMode.Transition(
            reconciled ? SystemMode.Ready : SystemMode.EntryHalted,
            reconciled ? "broker reconciliation complete" : string.Join(",", mismatches));

        return new ReconciliationResult(reconciled, mismatches, externalOrders, positionMismatches);
    }
}
