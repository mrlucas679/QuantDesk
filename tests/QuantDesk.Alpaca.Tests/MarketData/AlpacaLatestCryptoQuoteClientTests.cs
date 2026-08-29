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
            {"quotes":{"BTC/USD":{"ap":77705.10,"bp":77690.00}}}
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
}
