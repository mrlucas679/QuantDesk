using System.Net;
using System.Text;
using QuantDesk.Alpaca.Configuration;
using QuantDesk.Alpaca.MarketData;

namespace QuantDesk.Alpaca.Tests.MarketData;

public sealed class AlpacaHistoricalOptionBarClientTests
{
    [Fact]
    public async Task FetchesAllPagesAndDeduplicatesRequestedOccContracts()
    {
        var handler = new PagedHandler(
            """
            {"bars":{"SPY260904C00650000":[
              {"t":"2026-08-31T14:00:00Z","o":1,"h":2,"l":1,"c":1.5,"v":10,"n":2,"vw":1.4}]},
             "next_page_token":"next"}
            """,
            """
            {"bars":{"SPY260904C00650000":[
              {"t":"2026-08-31T14:05:00Z","o":1.5,"h":2.1,"l":1.4,"c":2,"v":12,"n":3,"vw":1.9}]},
             "next_page_token":null}
            """);
        using var httpClient = new HttpClient(handler);
        var client = new AlpacaHistoricalOptionBarClient(httpClient, Options());

        OptionBarQuery query = await client.GetBarsAsync(
            ["SPY260904C00650000"],
            DateTimeOffset.Parse("2026-08-31T14:00:00Z"),
            DateTimeOffset.Parse("2026-08-31T15:00:00Z"),
            "5Min",
            CancellationToken.None);

        Assert.Equal(2, query.Bars["SPY260904C00650000"].Count);
        Assert.Equal(2, query.RequestUris.Count);
        Assert.All(query.RequestUris, uri => Assert.DoesNotContain("test-secret", uri, StringComparison.Ordinal));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("/v1beta1/options/bars", handler.Requests[0].AbsolutePath);
        Assert.Contains("page_token=next", handler.Requests[1].Query);
    }

    [Fact]
    public async Task RejectsInvalidOrOversizedSymbolSetBeforeNetworkCall()
    {
        var handler = new PagedHandler("{}");
        using var httpClient = new HttpClient(handler);
        var client = new AlpacaHistoricalOptionBarClient(httpClient, Options());

        await Assert.ThrowsAsync<ArgumentException>(() => client.GetBarsAsync(
            ["NOT-OCC"], DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow,
            "1Day", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.GetBarsAsync(
            Enumerable.Range(0, 101).Select(index => $"SPY260904C{index + 1:00000000}").ToArray(),
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow,
            "1Day", CancellationToken.None));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RejectsBarOutsideTheRequestedWindow() =>
        await AssertRejectsAsync("""
            {"bars":{"SPY260904C00650000":[
              {"t":"2026-08-31T16:30:00Z","o":1,"h":2,"l":1,"c":1.5,"v":10,"n":2,"vw":1.4}]},
             "next_page_token":null}
            """);

    [Fact]
    public async Task RejectsBarWhoseOhlcValuesContradict() =>
        await AssertRejectsAsync("""
            {"bars":{"SPY260904C00650000":[
              {"t":"2026-08-31T14:00:00Z","o":1,"h":2,"l":1,"c":2.5,"v":10,"n":2,"vw":1.4}]},
             "next_page_token":null}
            """);

    [Fact]
    public async Task RejectsBarWithANonPositivePrice() =>
        await AssertRejectsAsync("""
            {"bars":{"SPY260904C00650000":[
              {"t":"2026-08-31T14:00:00Z","o":1,"h":2,"l":0,"c":1.5,"v":10,"n":2,"vw":1.4}]},
             "next_page_token":null}
            """);

    [Fact]
    public async Task RejectsUnrequestedSymbolInTheResponse() =>
        await AssertRejectsAsync("""
            {"bars":{"SPY260904P00650000":[
              {"t":"2026-08-31T14:00:00Z","o":1,"h":2,"l":1,"c":1.5,"v":10,"n":2,"vw":1.4}]},
             "next_page_token":null}
            """);

    [Fact]
    public async Task StopsInsteadOfLoopingWhenAlpacaRepeatsAPageToken()
    {
        var handler = new PagedHandler("""
            {"bars":{"SPY260904C00650000":[
              {"t":"2026-08-31T14:00:00Z","o":1,"h":2,"l":1,"c":1.5,"v":10,"n":2,"vw":1.4}]},
             "next_page_token":"same"}
            """);
        using var httpClient = new HttpClient(handler);
        var client = new AlpacaHistoricalOptionBarClient(httpClient, Options());

        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetBarsAsync(
            ["SPY260904C00650000"],
            DateTimeOffset.Parse("2026-08-31T14:00:00Z"),
            DateTimeOffset.Parse("2026-08-31T15:00:00Z"),
            "5Min",
            CancellationToken.None));
        Assert.Equal(2, handler.Requests.Count);
    }

    private static async Task AssertRejectsAsync(string page)
    {
        var handler = new PagedHandler(page);
        using var httpClient = new HttpClient(handler);
        var client = new AlpacaHistoricalOptionBarClient(httpClient, Options());

        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetBarsAsync(
            ["SPY260904C00650000"],
            DateTimeOffset.Parse("2026-08-31T14:00:00Z"),
            DateTimeOffset.Parse("2026-08-31T15:00:00Z"),
            "5Min",
            CancellationToken.None));
    }

    private static AlpacaOptions Options() => new()
    {
        BaseUrl = new Uri("https://paper-api.alpaca.markets"),
        DataBaseUrl = new Uri("https://data.alpaca.markets/"),
        KeyId = "test-key",
        SecretKey = "test-secret"
    };

    private sealed class PagedHandler(params string[] pages) : HttpMessageHandler
    {
        private int _index;
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            string page = pages[Math.Min(_index++, pages.Length - 1)];
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(page, Encoding.UTF8, "application/json")
            });
        }
    }
}
