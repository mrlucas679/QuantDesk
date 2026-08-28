using QuantDesk.Domain.Numerics;
using QuantDesk.Domain.Portfolio;
using QuantDesk.Domain.Trading;
using QuantDesk.Domain.Execution;
using QuantDesk.Runtime.Portfolio;
using QuantDesk.Runtime.Execution;
using QuantDesk.Runtime.Tests.TestData;

namespace QuantDesk.Runtime.Tests.Portfolio;

public sealed class PortfolioLedgerTests
{
    [Fact]
    public void TradeUpdateProcessorAppliesFillIdempotently()
    {
        var ledger = new PortfolioLedger(FinancialTestData.Portfolio());
        var processor = new TradeUpdateProcessor(ledger);
        var update = new BrokerTradeUpdate(BrokerTradeUpdateKind.Fill, "client", "broker", 1, 100, null, 10);
        Assert.True(processor.ApplyFill(update, 0, OrderSide.Buy));
        Assert.False(processor.ApplyFill(update, 0, OrderSide.Buy));
        Assert.Equal(1, ledger.Snapshot().Positions[0].Quantity);
    }

    [Fact]
    public void TradeUpdateProcessorRejectsUntimestampedFill()
    {
        var ledger = new PortfolioLedger(FinancialTestData.Portfolio());
        var processor = new TradeUpdateProcessor(ledger);
        var update = new BrokerTradeUpdate(BrokerTradeUpdateKind.Fill, "client", "broker", 1, 100, null, 0);
        Assert.False(processor.ApplyFill(update, 0, OrderSide.Buy));
        Assert.Empty(ledger.Snapshot().Positions);
    }

    [Fact]
    public void ApplyFill_IsIdempotentAndComputesRealizedPnl()
    {
        var ledger = new PortfolioLedger(FinancialTestData.Portfolio());
        ledger.RegisterOrderAttribution("buy-1", new OrderAttribution("trend", "exit-v1", 1, 1, []));
        ledger.RegisterOrderAttribution("sell-1", new OrderAttribution("trend", "exit-v1", 1, 1, []));
        var buy = new NormalizedFill("buy-1", "broker-1", 0, OrderSide.Buy, 2, 100, 1, "fill-1");
        var sell = new NormalizedFill("sell-1", "broker-2", 0, OrderSide.Sell, 1, 110, 2, "fill-2");

        Assert.True(ledger.ApplyFill(buy));
        Assert.False(ledger.ApplyFill(buy));
        Assert.True(ledger.ApplyFill(sell));

        PositionSnapshot position = Assert.Single(ledger.Snapshot().Positions);
        Assert.Equal(1, position.Quantity);
        Assert.Equal(100, position.AveragePrice);
        Assert.Equal(new Usd(10), position.RealizedPnl);
        Assert.Equal(2, ledger.Lots().Count);
    }

    [Fact]
    public void MarksOpenPositionAndEquityToMarket()
    {
        var ledger = new PortfolioLedger(new PortfolioSnapshot(0, new Usd(1000), new Usd(1000), new Usd(1000), Usd.Zero, Usd.Zero, Usd.Zero, Usd.Zero, 0, 0, 0, 0, 0, 0, 0, []));
        ledger.ApplyFill(new NormalizedFill("c", "b", 1, OrderSide.Buy, 2, 100, 1, "f"));
        ledger.MarkToMarket(new Dictionary<int, decimal> { [1] = 110 });
        Assert.Equal(1020, ledger.Snapshot().Equity.Value);
        Assert.Equal(20, ledger.Snapshot().Positions[0].UnrealizedPnl.Value);
    }

    [Fact]
    public void ApplyFill_TracksCashFromBrokerFill()
    {
        var ledger = new PortfolioLedger(FinancialTestData.Portfolio());
        var fill = new NormalizedFill("external", "broker-1", 0, OrderSide.Buy, 2, 100, 1, "fill-1");

        ledger.ApplyFill(fill);

        Assert.Equal(new Usd(9_800), ledger.Snapshot().Cash);
    }
}
