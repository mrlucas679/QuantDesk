using System.Net;
using System.Text;
using QuantDesk.Alpaca.Configuration;
using QuantDesk.Alpaca.MarketData;
using QuantDesk.Domain.Options;
using QuantDesk.Domain.Runtime;

namespace QuantDesk.Alpaca.Tests.MarketData;

public sealed class AlpacaLatestOptionQuoteClientTests
{
    private const string CallSymbol = "SPY260918C00600000";
    private const string PutSymbol = "SPY260918P00600000";
    private static readonly DateTimeOffset AsOf = new(2026, 8, 31, 15, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan MaximumAge = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task HealthyTwoSidedQuotesBecomeUsableSnapshots()
    {
        var handler = new StubHandler(Quotes(
            (CallSymbol, "{\"bp\":8.0,\"ap\":8.2,\"t\":\"2026-08-31T15:00:00Z\"}"),
            (PutSymbol, "{\"bp\":5.0,\"ap\":5.2,\"t\":\"2026-08-31T14:59:50Z\"}")));
        using var httpClient = new HttpClient(handler);
        var client = new AlpacaLatestOptionQuoteClient(httpClient, Options());

        IReadOnlyDictionary<int, OptionQuoteSnapshot> quotes = await client.GetQuotesAsync(
            new Dictionary<string, int> { [CallSymbol] = 11, [PutSymbol] = 22 },
            AsOf, MaximumAge, CancellationToken.None);

        Assert.Equal(DataQuality.Healthy, quotes[11].Quality);
        Assert.Equal(8.0, quotes[11].Bid);
        Assert.Equal(8.2, quotes[11].Ask);
        Assert.Equal(8.1, quotes[11].Mid, 6);
        Assert.Equal((8.2 - 8.0) / 8.1, quotes[11].RelativeSpread, 6);
        Assert.Equal(DataQuality.Healthy, quotes[22].Quality);
    }

    [Fact]
    public async Task AStaleQuoteIsMarkedUnusableRatherThanPricedFrom()
    {
        var handler = new StubHandler(Quotes(
            (CallSymbol, "{\"bp\":8.0,\"ap\":8.2,\"t\":\"2026-08-31T14:00:00Z\"}")));
        using var httpClient = new HttpClient(handler);
        var client = new AlpacaLatestOptionQuoteClient(httpClient, Options());

        IReadOnlyDictionary<int, OptionQuoteSnapshot> quotes = await client.GetQuotesAsync(
            new Dictionary<string, int> { [CallSymbol] = 11 }, AsOf, MaximumAge, CancellationToken.None);

        Assert.Equal(DataQuality.Stale, quotes[11].Quality);
    }

    [Theory]
    [InlineData("""{"bp":8.4,"ap":8.2,"t":"2026-08-31T15:00:00Z"}""")]  // crossed book
    [InlineData("""{"bp":8.0,"ap":0,"t":"2026-08-31T15:00:00Z"}""")]    // no offer
    [InlineData("""{"bp":8.0,"ap":8.2,"t":"not-a-time"}""")]            // unparseable timestamp
    public async Task AnUnusableQuoteIsMarkedStale(string quoteJson)
    {
        var handler = new StubHandler(Quotes((CallSymbol, quoteJson)));
        using var httpClient = new HttpClient(handler);
        var client = new AlpacaLatestOptionQuoteClient(httpClient, Options());

        IReadOnlyDictionary<int, OptionQuoteSnapshot> quotes = await client.GetQuotesAsync(
            new Dictionary<string, int> { [CallSymbol] = 11 }, AsOf, MaximumAge, CancellationToken.None);

        Assert.Equal(DataQuality.Stale, quotes[11].Quality);
    }

    [Fact]
    public async Task AContractTheVenueOmittedStillYieldsAnUnusableSnapshot()
    {
        var handler = new StubHandler("""{"quotes":{}}""");
        using var httpClient = new HttpClient(handler);
        var client = new AlpacaLatestOptionQuoteClient(httpClient, Options());

        IReadOnlyDictionary<int, OptionQuoteSnapshot> quotes = await client.GetQuotesAsync(
            new Dictionary<string, int> { [CallSymbol] = 11 }, AsOf, MaximumAge, CancellationToken.None);

        // Every requested slot must be represented, or a caller could mistake absence for health.
        Assert.Single(quotes);
        Assert.Equal(DataQuality.Stale, quotes[11].Quality);
    }

    [Fact]
    public async Task AnUnrequestedSymbolFailsTheAcquisition()
    {
        var handler = new StubHandler(Quotes(
            (PutSymbol, "{\"bp\":5.0,\"ap\":5.2,\"t\":\"2026-08-31T15:00:00Z\"}")));
        using var httpClient = new HttpClient(handler);
        var client = new AlpacaLatestOptionQuoteClient(httpClient, Options());

        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetQuotesAsync(
            new Dictionary<string, int> { [CallSymbol] = 11 }, AsOf, MaximumAge, CancellationToken.None));
    }

    [Fact]
    public async Task InvalidRequestsAreRejectedBeforeAnyNetworkCall()
    {
        var handler = new StubHandler("{}");
        using var httpClient = new HttpClient(handler);
        var client = new AlpacaLatestOptionQuoteClient(httpClient, Options());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.GetQuotesAsync(
            new Dictionary<string, int>(), AsOf, MaximumAge, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetQuotesAsync(
            new Dictionary<string, int> { ["NOT-OCC"] = 1 }, AsOf, MaximumAge, CancellationToken.None));

        Assert.Empty(handler.Requests);
    }

    /// <summary>Builds a quotes payload without raw-string brace ambiguity.</summary>
    private static string Quotes(params (string Symbol, string Quote)[] entries) =>
        "{\"quotes\":{" +
        string.Join(',', entries.Select(entry => "\"" + entry.Symbol + "\":" + entry.Quote)) +
        "}}";

    private static AlpacaOptions Options() => new()
    {
        BaseUrl = new Uri("https://paper-api.alpaca.markets"),
        DataBaseUrl = new Uri("https://data.alpaca.markets/"),
        KeyId = "test-key",
        SecretKey = "test-secret"
    };

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
