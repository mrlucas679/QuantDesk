using System.Net;
using System.Text;
using QuantDesk.Alpaca.Configuration;
using QuantDesk.Alpaca.MarketData;

namespace QuantDesk.Alpaca.Tests.MarketData;

public sealed class AlpacaLatestEquityQuoteClientTests
{
    [Fact]
    public async Task ReturnsTheLiveSpreadAndRetainsHistoryForIndicatorWarmUp()
    {
        var handler = new SequencedHandler(
            """{"quotes":{"SPY":{"ap":601.25,"bp":601.23}}}""",
            BarsPage(20));
        using var httpClient = new HttpClient(handler);
        var client = new AlpacaLatestEquityQuoteClient(httpClient, Options());

        DirectionalMarketEvidence evidence = await client.GetEvidenceAsync("SPY", CancellationToken.None);

        Assert.Equal(601.23m, evidence.Bid);
        Assert.Equal(601.25m, evidence.Ask);
        // Every bar returned is retained, not truncated to the gate's 13-bar minimum. The gate
        // still reads the last thirteen; the indicators need far more, and a 48-period average
        // seeded on thirteen bars returns a number that looks valid and is wrong.
        Assert.Equal(20, evidence.Closes.Count);
        // The window must end on the newest close.
        Assert.Equal(119m, evidence.Closes[^1]);
        Assert.Equal(100m, evidence.Closes[0]);
        Assert.Contains("feed=iex", handler.Requests[0].Query, StringComparison.Ordinal);
        Assert.Contains("adjustment=all", handler.Requests[1].Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AShortBarSeriesIsReturnedIntactSoTheGateCanRejectIt()
    {
        var handler = new SequencedHandler(
            """{"quotes":{"SPY":{"ap":601.25,"bp":601.23}}}""",
            BarsPage(4));
        using var httpClient = new HttpClient(handler);
        var client = new AlpacaLatestEquityQuoteClient(httpClient, Options());

        DirectionalMarketEvidence evidence = await client.GetEvidenceAsync("SPY", CancellationToken.None);

        // Padding a short series would fabricate momentum. The client hands the gate what exists.
        Assert.Equal(4, evidence.Closes.Count);
    }

    [Theory]
    [InlineData("""{"quotes":{"SPY":{"ap":601.25,"bp":0}}}""")]
    [InlineData("""{"quotes":{"SPY":{"ap":600.00,"bp":601.23}}}""")]
    [InlineData("""{"quotes":{}}""")]
    [InlineData("""{"quotes":{"QQQ":{"ap":601.25,"bp":601.23}}}""")]
    public async Task AnInvalidOrMissingSpreadIsRejectedRatherThanTraded(string quotePayload)
    {
        var handler = new SequencedHandler(quotePayload, BarsPage(20));
        using var httpClient = new HttpClient(handler);
        var client = new AlpacaLatestEquityQuoteClient(httpClient, Options());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetEvidenceAsync("SPY", CancellationToken.None));
    }

    [Theory]
    [InlineData("BTC/USD")]
    [InlineData("TOOLONG")]
    [InlineData("SP-Y")]
    public async Task NonEquitySymbolsAreRejectedBeforeAnyNetworkCall(string symbol)
    {
        var handler = new SequencedHandler("{}");
        using var httpClient = new HttpClient(handler);
        var client = new AlpacaLatestEquityQuoteClient(httpClient, Options());

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.GetEvidenceAsync(symbol, CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    private static string BarsPage(int count)
    {
        string bars = string.Join(',', Enumerable.Range(0, count)
            .Select(index => $"{{\"c\":{100 + index}}}"));
        return "{\"bars\":{\"SPY\":[" + bars + "]}}";
    }

    private static AlpacaOptions Options() => new()
    {
        BaseUrl = new Uri("https://paper-api.alpaca.markets"),
        DataBaseUrl = new Uri("https://data.alpaca.markets/"),
        KeyId = "test-key",
        SecretKey = "test-secret"
    };

    private sealed class SequencedHandler(params string[] responses) : HttpMessageHandler
    {
        private int _index;
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            string body = responses[Math.Min(_index++, responses.Length - 1)];
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
