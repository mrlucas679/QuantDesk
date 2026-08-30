using QuantDesk.Alpaca.Trading;
using QuantDesk.Domain.Execution;

namespace QuantDesk.Alpaca.Tests;

public sealed class AlpacaTradeUpdateParserTests
{
    [Fact]
    public void ParsesNestedFillUpdate()
    {
        const string json = "{\"event\":\"fill\",\"timestamp\":\"2026-08-28T12:00:00Z\",\"order\":{\"id\":\"broker\",\"client_order_id\":\"client\",\"filled_qty\":\"2\",\"filled_avg_price\":\"101.5\"}}";
        Assert.True(AlpacaTradeUpdateParser.TryParse(json, out BrokerTradeUpdate update));
        Assert.Equal(BrokerTradeUpdateKind.Fill, update.Kind);
        Assert.Equal("client", update.ClientOrderId);
        Assert.Equal(2, update.FilledQuantity);
        Assert.Equal(101.5m, update.FilledPrice);
    }

    [Fact]
    public void ParsesPaperStreamEnvelope()
    {
        const string json = "{\"stream\":\"trade_updates\",\"data\":{\"event\":\"fill\",\"timestamp\":\"2026-08-28T12:00:00Z\",\"order\":{\"id\":\"broker\",\"client_order_id\":\"client\",\"filled_qty\":\"2\",\"filled_avg_price\":\"101.5\"}}}";

        Assert.True(AlpacaTradeUpdateParser.TryParse(json, out BrokerTradeUpdate update));
        Assert.Equal(BrokerTradeUpdateKind.Fill, update.Kind);
        Assert.Equal("client", update.ClientOrderId);
    }
}
