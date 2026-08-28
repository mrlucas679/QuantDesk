using QuantDesk.Domain.Execution;
using QuantDesk.Domain.Numerics;
using QuantDesk.Domain.Portfolio;
using QuantDesk.Domain.Strategies;
using QuantDesk.Domain.Trading;
using QuantDesk.Runtime.Execution;
using QuantDesk.Runtime.Persistence;
using QuantDesk.Runtime.Portfolio;
using QuantDesk.Runtime.Positions;
using QuantDesk.Runtime.Tests.TestData;

namespace QuantDesk.Runtime.Tests.Integration;

public sealed class PaperTradingLifecycleTests
{
    [Fact]
    public void FillMarkAndExitLifecycleIsDeterministic()
    {
        var ledger = new PortfolioLedger(FinancialTestData.Portfolio());
        var processor = new TradeUpdateProcessor(ledger);
        var fill = new BrokerTradeUpdate(BrokerTradeUpdateKind.Fill, "client-1", "broker-1", 2, 100, null, 1);
        Assert.True(processor.ApplyFill(fill, 0, OrderSide.Buy));
        ledger.MarkToMarket(new Dictionary<int, decimal> { [0] = 94 });
        Assert.Equal(-12, ledger.Snapshot().Positions[0].UnrealizedPnl.Value);

        var plan = new PositionManagementPlan(TimeSpan.FromMinutes(5), false, false, new Usd(10), null, "exit-v1");
        ExitEvaluation exit = new ExitEngine().Evaluate(plan, 0, 1, ledger.Snapshot().Positions[0].UnrealizedPnl, true, true);
        Assert.True(exit.ShouldExit);
        Assert.Equal(ExitReason.MaximumAdverseLoss, exit.Reason);
    }
}
