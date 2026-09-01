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

    [Fact]
    public async Task ABarWithoutAVwapIsKeptWithTheValueLeftUnreported()
    {
        // A thin option can produce a bar the venue does not weight. Recording that as a zero vwap
        // would assert a price the venue never quoted, and rejecting the bar would drop real trades.
        var handler = new PagedHandler(
            """
            {"bars":{"SPY260904C00650000":[
              {"t":"2026-08-31T14:00:00Z","o":1,"h":2,"l":1,"c":1.5,"v":10}]},
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

        HistoricalOptionBar bar = Assert.Single(query.Bars["SPY260904C00650000"]);
        Assert.Null(bar.Vwap);
        Assert.Null(bar.TradeCount);
        Assert.Equal(1.5m, bar.Close);
    }

    [Fact]
    public async Task AReportedButNonPositiveVwapIsStillRejected()
    {
        // Absent means unreported; zero means the venue stated a price that cannot be true.
        await AssertRejectsAsync(
            """
            {"bars":{"SPY260904C00650000":[
              {"t":"2026-08-31T14:00:00Z","o":1,"h":2,"l":1,"c":1.5,"v":10,"n":2,"vw":0}]},
             "next_page_token":null}
            """);
    }

    [Fact]
    public async Task AnEmptyBarsPayloadYieldsAnEmptySeriesRatherThanAFailure()
    {
        // Alpaca answers a window with no trading by omitting bars entirely.
        var handler = new PagedHandler("""{"bars":null,"next_page_token":null}""");
        using var httpClient = new HttpClient(handler);
        var client = new AlpacaHistoricalOptionBarClient(httpClient, Options());

        OptionBarQuery query = await client.GetBarsAsync(
            ["SPY260904C00650000"],
            DateTimeOffset.Parse("2026-08-31T14:00:00Z"),
            DateTimeOffset.Parse("2026-08-31T15:00:00Z"),
            "5Min",
            CancellationToken.None);

        Assert.Empty(query.Bars["SPY260904C00650000"]);
    }

    [Fact]
    public async Task AVenueRefusalReportsTheStatusAndTheVenuesOwnExplanation()
    {
        var handler = new PagedHandler(
            HttpStatusCode.Unauthorized,
            """{"code":40110000,"message":"invalid or missing credentials"}""");
        using var httpClient = new HttpClient(handler);
        var client = new AlpacaHistoricalOptionBarClient(httpClient, Options());

        HttpRequestException failure = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetBarsAsync(
                ["SPY260904C00650000"],
                DateTimeOffset.Parse("2026-08-31T14:00:00Z"),
                DateTimeOffset.Parse("2026-08-31T15:00:00Z"),
                "5Min",
                CancellationToken.None));

        Assert.Equal(HttpStatusCode.Unauthorized, failure.StatusCode);
        Assert.Contains("v1beta1/options/bars", failure.Message, StringComparison.Ordinal);
        Assert.Contains("code 40110000", failure.Message, StringComparison.Ordinal);
        Assert.Contains("invalid or missing credentials", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("test-secret", failure.Message, StringComparison.Ordinal);
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

    private sealed class PagedHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string[] _pages;
        private int _index;

        public PagedHandler(params string[] pages) : this(HttpStatusCode.OK, pages) { }

        public PagedHandler(HttpStatusCode status, params string[] pages)
        {
            _status = status;
            _pages = pages;
        }

        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            string page = _pages[Math.Min(_index++, _pages.Length - 1)];
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(page, Encoding.UTF8, "application/json")
            });
        }
    }
}
