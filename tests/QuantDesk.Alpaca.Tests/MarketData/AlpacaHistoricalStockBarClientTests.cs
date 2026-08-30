using System.Net;
using System.Text;
using QuantDesk.Alpaca.Configuration;
using QuantDesk.Alpaca.MarketData;

namespace QuantDesk.Alpaca.Tests.MarketData;

public sealed class AlpacaHistoricalStockBarClientTests
{
    [Fact]
    public async Task GetBarsAsync_FollowsPaginationAndReturnsChronologicalUniqueBars()
    {
        var handler = new PagedHandler(
            """{"bars":{"SPY":[{"t":"2026-01-02T00:00:00Z","o":2,"h":3,"l":1,"c":2.5,"v":10,"n":2,"vw":2.4}]},"next_page_token":"next"}""",
            """{"bars":{"SPY":[{"t":"2026-01-01T00:00:00Z","o":1,"h":2,"l":0.5,"c":1.5,"v":8,"n":1,"vw":1.4},{"t":"2026-01-02T00:00:00Z","o":2,"h":3,"l":1,"c":2.5,"v":10,"n":2,"vw":2.4}]},"next_page_token":null}""");
        using var httpClient = new HttpClient(handler);
        var client = new AlpacaHistoricalStockBarClient(httpClient, TestOptions());

        IReadOnlyList<HistoricalStockBar> bars = await client.GetBarsAsync(
            "SPY", DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            DateTimeOffset.Parse("2026-01-03T00:00:00Z"), "5Min", CancellationToken.None);

        Assert.Equal(2, bars.Count);
        Assert.True(bars[0].Timestamp < bars[1].Timestamp);
        Assert.Contains("feed=iex", handler.RequestUris[0].Query);
        Assert.Contains("page_token=next", handler.RequestUris[1].Query);
    }

    private static AlpacaOptions TestOptions() => new()
    {
        BaseUrl = new Uri("https://paper-api.alpaca.markets"), KeyId = "test-key", SecretKey = "test-secret"
    };

    private sealed class PagedHandler(params string[] responses) : HttpMessageHandler
    {
        private int _index;
        public List<Uri> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responses[_index++], Encoding.UTF8, "application/json")
            });
        }
    }
}
