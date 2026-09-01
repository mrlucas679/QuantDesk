using QuantDesk.Domain.Execution;
using QuantDesk.Domain.Trading;
using QuantDesk.Runtime.Persistence;

namespace QuantDesk.Api.PaperTrading;

/// <summary>
/// Builds the three broker commands the diagnostic lane sends: entry, managed exit, and emergency
/// flatten.
///
/// Extracted from a 1,072-line service with 37 methods. Command construction is pure — a record
/// and a clock reading in, a command out — so it was the safest seam to pull first, and pulling it
/// makes the three commands directly comparable side by side. Their differences are the whole
/// point: entry buys by notional and exits sell by quantity, and each carries a distinct priority
/// so the execution layer can order a risk-reducing exit ahead of an exploratory entry.
/// </summary>
public static class DiagnosticCommandFactory
{
    /// <summary>Strategy identifiers, kept here so all three stay visibly consistent.</summary>
    public const string EntryStrategyId = "diagnostic-execution";
    public const string ExitStrategyId = "diagnostic-execution-exit";
    public const string EmergencyStrategyId = "diagnostic-emergency-flatten";

    /// <summary>
    /// Entry is sized by <em>notional</em> rather than quantity, so the venue computes the
    /// quantity from its own price and the order cannot be rejected for a stale quantity.
    /// </summary>
    public static ExecutionCommand Entry(
        DiagnosticExecutionRecord record, int instrumentSlot, decimal quantity, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.EntryClientOrderId);

        return Build(
            record.EntryClientOrderId!, instrumentSlot, OrderSide.Buy, PositionIntent.Open,
            ExecutionPriority.ExplorationEntry, quantity, EntryStrategyId, now) with
        {
            Notional = record.RequestedNotional
        };
    }

    /// <summary>Managed exit, sized by the quantity actually held.</summary>
    public static ExecutionCommand Exit(
        DiagnosticExecutionRecord record, int instrumentSlot, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.ExitClientOrderId);

        return Build(
            record.ExitClientOrderId!, instrumentSlot, OrderSide.Sell, PositionIntent.Close,
            ExecutionPriority.NormalExit, record.ExitQuantity, ExitStrategyId, now);
    }

    /// <summary>
    /// Emergency flatten. Carries the highest priority so it is never queued behind an entry.
    /// </summary>
    public static ExecutionCommand EmergencyFlatten(
        DiagnosticExecutionRecord record, int instrumentSlot, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.EmergencyClientOrderId);

        return Build(
            record.EmergencyClientOrderId!, instrumentSlot, OrderSide.Sell, PositionIntent.Close,
            ExecutionPriority.EmergencyExit, record.EmergencyFlattenQuantity,
            EmergencyStrategyId, now);
    }

    private static ExecutionCommand Build(
        string clientOrderId,
        int instrumentSlot,
        OrderSide side,
        PositionIntent intent,
        ExecutionPriority priority,
        decimal quantity,
        string strategyId,
        DateTimeOffset now)
    {
        long timestamp = now.ToUnixTimeMilliseconds();
        return new ExecutionCommand(
            CommandId: timestamp,
            priority,
            RiskReservationId: 0,
            CapitalReservationId: 0,
            clientOrderId,
            instrumentSlot,
            side,
            intent,
            ExecutionOrderType.Market,
            ExecutionTimeInForce.Gtc,
            quantity,
            LimitPrice: null,
            CreatedMonotonicTicks: timestamp,
            // The diagnostic lane deliberately does not expire its commands: the durable record,
            // not a wall-clock fence, decides when an order is abandoned.
            ExpiresMonotonicTicks: long.MaxValue,
            strategyId);
    }
}
