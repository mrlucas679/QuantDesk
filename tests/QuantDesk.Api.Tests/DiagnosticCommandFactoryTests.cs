using QuantDesk.Api.PaperTrading;
using QuantDesk.Domain.Execution;
using QuantDesk.Domain.Trading;
using QuantDesk.Runtime.Persistence;

namespace QuantDesk.Api.Tests;

/// <summary>
/// Command construction was buried in a 1,072-line service and had no direct coverage. Extracting
/// it made these properties testable, and they are the ones that decide whether an order is
/// correct: side, intent, sizing basis, and the priority that orders a risk-reducing exit ahead of
/// an exploratory entry.
/// </summary>
public sealed class DiagnosticCommandFactoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EntryBuysToOpenAndIsSizedByNotionalNotQuantity()
    {
        ExecutionCommand command = DiagnosticCommandFactory.Entry(Record(), 0, 0.5m, Now);

        Assert.Equal(OrderSide.Buy, command.Side);
        Assert.Equal(PositionIntent.Open, command.PositionIntent);
        // Sizing by notional lets the venue compute quantity from its own price, so the order
        // cannot be rejected for a stale quantity.
        Assert.Equal(10m, command.Notional);
        Assert.Equal("entry-client-id", command.ClientOrderId);
        Assert.Equal(DiagnosticCommandFactory.EntryStrategyId, command.StrategyId);
    }

    [Fact]
    public void ExitSellsToCloseTheQuantityActuallyHeld()
    {
        ExecutionCommand command = DiagnosticCommandFactory.Exit(Record(), 0, Now);

        Assert.Equal(OrderSide.Sell, command.Side);
        Assert.Equal(PositionIntent.Close, command.PositionIntent);
        Assert.Equal(0.25m, command.Quantity);
        // An exit must never carry a notional: it closes a position, it does not size a new one.
        Assert.Null(command.Notional);
        Assert.Equal("exit-client-id", command.ClientOrderId);
    }

    [Fact]
    public void EmergencyFlattenSellsTheFlattenQuantity()
    {
        ExecutionCommand command = DiagnosticCommandFactory.EmergencyFlatten(Record(), 0, Now);

        Assert.Equal(OrderSide.Sell, command.Side);
        Assert.Equal(0.75m, command.Quantity);
        Assert.Equal("emergency-client-id", command.ClientOrderId);
    }

    [Fact]
    public void PrioritiesOrderRiskReductionAheadOfExploration()
    {
        DiagnosticExecutionRecord record = Record();

        Assert.Equal(
            ExecutionPriority.ExplorationEntry,
            DiagnosticCommandFactory.Entry(record, 0, 1m, Now).Priority);
        Assert.Equal(
            ExecutionPriority.NormalExit,
            DiagnosticCommandFactory.Exit(record, 0, Now).Priority);
        // The emergency flatten must never queue behind an entry.
        Assert.Equal(
            ExecutionPriority.EmergencyExit,
            DiagnosticCommandFactory.EmergencyFlatten(record, 0, Now).Priority);
    }

    [Fact]
    public void EveryCommandIsAMarketOrderHeldGoodTillCancelled()
    {
        DiagnosticExecutionRecord record = Record();

        foreach (ExecutionCommand command in new[]
        {
            DiagnosticCommandFactory.Entry(record, 0, 1m, Now),
            DiagnosticCommandFactory.Exit(record, 0, Now),
            DiagnosticCommandFactory.EmergencyFlatten(record, 0, Now)
        })
        {
            Assert.Equal(ExecutionOrderType.Market, command.OrderType);
            Assert.Equal(ExecutionTimeInForce.Gtc, command.TimeInForce);
            Assert.Null(command.LimitPrice);
            // The durable record decides abandonment, not a wall-clock fence on the command.
            Assert.Equal(long.MaxValue, command.ExpiresMonotonicTicks);
        }
    }

    [Fact]
    public void TheSameClockReadingProducesTheSameCommandIdentity()
    {
        DiagnosticExecutionRecord record = Record();

        Assert.Equal(
            DiagnosticCommandFactory.Entry(record, 0, 1m, Now).CommandId,
            DiagnosticCommandFactory.Entry(record, 0, 1m, Now).CommandId);
        Assert.NotEqual(
            DiagnosticCommandFactory.Entry(record, 0, 1m, Now).CommandId,
            DiagnosticCommandFactory.Entry(record, 0, 1m, Now.AddSeconds(1)).CommandId);
    }

    [Fact]
    public void ARecordWithoutTheRequiredClientOrderIdIsRejected()
    {
        // A command with no client order ID cannot be recovered after an ambiguous POST, so it
        // must never be constructed in the first place.
        Assert.ThrowsAny<ArgumentException>(
            () => DiagnosticCommandFactory.Entry(Record(entryId: null), 0, 1m, Now));
        Assert.ThrowsAny<ArgumentException>(
            () => DiagnosticCommandFactory.Exit(Record(exitId: null), 0, Now));
        Assert.ThrowsAny<ArgumentException>(
            () => DiagnosticCommandFactory.EmergencyFlatten(Record(emergencyId: null), 0, Now));
    }

    [Fact]
    public void ANullRecordIsRejected() =>
        Assert.Throws<ArgumentNullException>(
            () => DiagnosticCommandFactory.Entry(null!, 0, 1m, Now));

    private static DiagnosticExecutionRecord Record(
        string? entryId = "entry-client-id",
        string? exitId = "exit-client-id",
        string? emergencyId = "emergency-client-id") =>
        new("EXP-1", "diagnostic", "BTC/USD", "EntryReserved",
            RequestedNotional: 10m, HoldingDuration: TimeSpan.FromMinutes(2), CreatedAt: Now,
            EntryClientOrderId: entryId, ExitClientOrderId: exitId)
        {
            EmergencyClientOrderId = emergencyId,
            ExitQuantity = 0.25m,
            EmergencyFlattenQuantity = 0.75m
        };
}
