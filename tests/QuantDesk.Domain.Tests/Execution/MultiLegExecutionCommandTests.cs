using QuantDesk.Domain.Execution;
using QuantDesk.Domain.Trading;

namespace QuantDesk.Domain.Tests.Execution;

public sealed class MultiLegExecutionCommandTests
{
    [Fact]
    public void DefinedRiskVerticalHasValidAtomicSemantics()
    {
        var command = new MultiLegExecutionCommand(
            "qd-spy-vertical-1", 1, ExecutionOrderType.Limit, ExecutionTimeInForce.Day, 1.25m,
            [
                new("SPY260904C00650000", 1, OrderSide.Buy, PositionIntent.BuyToOpen),
                new("SPY260904C00655000", 1, OrderSide.Sell, PositionIntent.SellToOpen)
            ]);

        Assert.True(command.IsValid());
    }

    [Fact]
    public void RejectsDifferentUnderlyingsOrNonReducedRatios()
    {
        var differentUnderlying = new MultiLegExecutionCommand(
            "qd-invalid-1", 1, ExecutionOrderType.Limit, ExecutionTimeInForce.Day, 1m,
            [
                new("SPY260904C00650000", 1, OrderSide.Buy, PositionIntent.BuyToOpen),
                new("QQQ260904C00655000", 1, OrderSide.Sell, PositionIntent.SellToOpen)
            ]);
        var ratios = differentUnderlying with
        {
            Legs = [
                new("SPY260904C00650000", 2, OrderSide.Buy, PositionIntent.BuyToOpen),
                new("SPY260904C00655000", 4, OrderSide.Sell, PositionIntent.SellToOpen)
            ]
        };

        Assert.False(differentUnderlying.IsValid());
        Assert.False(ratios.IsValid());
    }
}
