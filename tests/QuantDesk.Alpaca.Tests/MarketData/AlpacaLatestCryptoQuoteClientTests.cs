using System.Net;
using System.Text;
using QuantDesk.Alpaca.Configuration;
using QuantDesk.Alpaca.MarketData;

namespace QuantDesk.Alpaca.Tests.MarketData;

public sealed class AlpacaLatestCryptoQuoteClientTests
{
    [Fact]
    public async Task GetAskAsync_ReadsNumericCryptoQuote()
    {
        var handler = new QuoteHandler("""
            {"quotes":{"BTC/USD":{"ap":77705.10,"bp":77690.00,"bs":0.25,"as":0.50}}}
            """);
        using var httpClient = new HttpClient(handler);
        var client = new AlpacaLatestCryptoQuoteClient(httpClient, new AlpacaOptions
        {
            BaseUrl = new Uri("https://paper-api.alpaca.markets"),
            KeyId = "test-key",
            SecretKey = "test-secret"
        });

        decimal ask = await client.GetAskAsync("BTC/USD", CancellationToken.None);

        Assert.Equal(77705.10m, ask);
        Assert.Contains("symbols=BTC%2FUSD", handler.RequestUri!.Query);
    }

    [Fact]
    public async Task GetLatestQuoteAsync_ReadsBothSidesWithoutFetchingBars()
    {
        var handler = new QuoteHandler("""
            {"quotes":{"BTC/USD":{"ap":77705.10,"bp":77690.00,"bs":0.25,"as":0.50}}}
            """);
        using var httpClient = new HttpClient(handler);
        var client = new AlpacaLatestCryptoQuoteClient(httpClient, new AlpacaOptions
        {
            BaseUrl = new Uri("https://paper-api.alpaca.markets"),
            KeyId = "test-key",
            SecretKey = "test-secret"
        });

        CryptoQuoteSnapshot quote = await client.GetLatestQuoteAsync("BTC/USD", CancellationToken.None);

        Assert.Equal(77690.00m, quote.Bid);
        Assert.Equal(77705.10m, quote.Ask);
        Assert.Equal(0.25m, quote.BidSize);
        Assert.Equal(0.50m, quote.AskSize);
        Assert.Contains("/latest/quotes", handler.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task GetHistoricalBarsAsync_FollowsPaginationAndOrdersUniqueBars()
    {
        var handler = new PagedHandler(
            """{"bars":{"BTC/USD":[{"t":"2026-01-02T00:00:00Z","o":2,"h":3,"l":1,"c":2.5,"v":10,"n":2,"vw":2.4}]},"next_page_token":"next"}""",
            """{"bars":{"BTC/USD":[{"t":"2026-01-01T00:00:00Z","o":1,"h":2,"l":0.5,"c":1.5,"v":8,"n":1,"vw":1.4},{"t":"2026-01-02T00:00:00Z","o":2,"h":3,"l":1,"c":2.5,"v":10,"n":2,"vw":2.4}]},"next_page_token":null}""");
        using var httpClient = new HttpClient(handler);
        var client = new AlpacaLatestCryptoQuoteClient(httpClient, new AlpacaOptions
        {
            BaseUrl = new Uri("https://paper-api.alpaca.markets"),
            KeyId = "test-key",
            SecretKey = "test-secret"
        });

        IReadOnlyList<HistoricalCryptoBar> bars = await client.GetHistoricalBarsAsync(
            "BTC/USD", DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            DateTimeOffset.Parse("2026-01-03T00:00:00Z"), "5Min", CancellationToken.None);

        Assert.Equal(2, bars.Count);
        Assert.True(bars[0].Timestamp < bars[1].Timestamp);
        Assert.Contains("page_token=next", handler.RequestUris[1].Query);
    }

    private sealed class QuoteHandler(string responseBody) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class PagedHandler(params string[] responses) : HttpMessageHandler
    {
        private int _index;
        public List<Uri> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!);
            string body = responses[_index++];
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
