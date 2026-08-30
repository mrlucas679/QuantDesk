using QuantDesk.Alpaca.MarketData;
using QuantDesk.Domain.Market;

namespace QuantDesk.Alpaca.Tests;

public sealed class MarketDataParserTests
{
    [Theory]
    [InlineData("[{\"T\":\"success\",\"msg\":\"connected\"}]", "connected", true)]
    [InlineData("[{\"T\":\"success\",\"msg\":\"authenticated\"}]", "authenticated", true)]
    [InlineData("[{\"T\":\"error\",\"code\":402,\"msg\":\"auth failed\"}]", "authenticated", false)]
    [InlineData("not-json", "authenticated", false)]
    public void HandshakeValidator_AcceptsOnlyExpectedSuccessMessage(string payload, string expected, bool accepted)
    {
        Assert.Equal(accepted, AlpacaStreamHandshake.IsSuccess(payload, expected));
    }

    [Fact]
    public void SubscriptionValidator_RequiresEveryRequestedMarketDataChannel()
    {
        const string accepted = "[{\"T\":\"subscription\",\"trades\":[\"BTC/USD\"],\"quotes\":[\"BTC/USD\"],\"orderbooks\":[\"BTC/USD\"]}]";
        const string missingTrade = "[{\"T\":\"subscription\",\"trades\":[],\"quotes\":[\"BTC/USD\"]}]";

        Assert.True(AlpacaStreamHandshake.IsSubscriptionAccepted(accepted, ["BTC/USD"]));
        Assert.False(AlpacaStreamHandshake.IsSubscriptionAccepted(missingTrade, ["BTC/USD"]));
        Assert.False(AlpacaStreamHandshake.IsSubscriptionAccepted("[{\"T\":\"error\",\"msg\":\"invalid subscription\"}]", ["BTC/USD"]));
    }

    [Fact]
    public void ParsesValidQuoteAndRejectsInvalidSpread()
    {
        var parser = new AlpacaMarketDataParser(new Dictionary<string, int> { ["AAPL"] = 7 });
        Assert.True(parser.TryParse("{\"T\":\"q\",\"S\":\"AAPL\",\"bp\":100.1,\"ap\":100.2,\"bs\":2,\"as\":3,\"t\":\"2026-08-28T12:00:00Z\",\"i\":9}", 10, out NormalizedMarketEvent value));
        Assert.Equal(MarketEventKind.Quote, value.Kind);
        Assert.Equal(7, value.Quote.InstrumentSlot);
        Assert.False(parser.TryParse("{\"T\":\"q\",\"S\":\"AAPL\",\"bp\":101,\"ap\":100,\"t\":\"2026-08-28T12:00:00Z\"}", 10, out _));
    }

    [Fact]
    public void RejectsUnknownSymbolAndMalformedTrade()
    {
        var parser = new AlpacaMarketDataParser(new Dictionary<string, int> { ["AAPL"] = 1 });
        Assert.False(parser.TryParse("{\"T\":\"t\",\"S\":\"MSFT\",\"p\":1,\"s\":1}", 1, out _));
        Assert.False(parser.TryParse("{\"T\":\"t\",\"S\":\"AAPL\",\"p\":0,\"s\":1}", 1, out _));
    }

    [Fact]
    public void ParsesAlpacaArrayBatchEnvelope()
    {
        var parser = new AlpacaMarketDataParser(new Dictionary<string, int> { ["AAPL"] = 1 });
        const string payload = "[{\"T\":\"q\",\"S\":\"AAPL\",\"bp\":100,\"ap\":101,\"t\":\"2026-08-28T12:00:00Z\"}]";
        Assert.True(parser.TryParse(payload, 1, out NormalizedMarketEvent value));
        Assert.Equal(MarketEventKind.Quote, value.Kind);
        const string batch = "[{\"T\":\"q\",\"S\":\"AAPL\",\"bp\":100,\"ap\":101},{\"T\":\"t\",\"S\":\"AAPL\",\"p\":100.5,\"s\":2}]";
        Assert.Equal(2, parser.ParseMany(batch, 1).Count);
    }

    [Fact]
    public void ParsesOrderBookResetAndIncrementalDepth()
    {
        var parser = new AlpacaMarketDataParser(new Dictionary<string, int> { ["BTC/USD"] = 1 });
        const string reset = "{\"T\":\"o\",\"S\":\"BTC/USD\",\"t\":\"2026-08-28T12:00:00Z\",\"r\":true,\"b\":[{\"p\":100,\"s\":2}],\"a\":[{\"p\":101,\"s\":3}]}";
        const string incremental = "{\"T\":\"o\",\"S\":\"BTC/USD\",\"t\":\"2026-08-28T12:00:01Z\",\"b\":[{\"p\":100,\"s\":4}],\"a\":[]}";

        Assert.True(parser.TryParse(reset, 1, out NormalizedMarketEvent initial));
        Assert.Equal(MarketEventKind.OrderBook, initial.Kind);
        Assert.Equal(2, initial.OrderBook.BidDepth);
        Assert.Equal(3, initial.OrderBook.AskDepth);
        Assert.True(parser.TryParse(incremental, 2, out NormalizedMarketEvent updated));
        Assert.Equal(4, updated.OrderBook.BidDepth);
        Assert.Equal(3, updated.OrderBook.AskDepth);
    }

    [Fact]
    public void StreamRejectsInsecureEndpointAndMissingCredentials()
    {
        var parser = new AlpacaMarketDataParser(new Dictionary<string, int>());
        Assert.Throws<ArgumentException>(() => new AlpacaMarketDataStream(new Uri("ws://localhost"), "key", "secret", parser));
        Assert.Throws<ArgumentException>(() => new AlpacaMarketDataStream(new Uri("wss://localhost"), "", "secret", parser));
        Assert.False(parser.TryParse("{malformed", 1, out _));
    }
}
