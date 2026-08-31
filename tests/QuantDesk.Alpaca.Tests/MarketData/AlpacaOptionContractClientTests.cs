using System.Net;
using System.Text;
using QuantDesk.Alpaca.Configuration;
using QuantDesk.Alpaca.MarketData;
using QuantDesk.Domain.Options;

namespace QuantDesk.Alpaca.Tests.MarketData;

public sealed class AlpacaOptionContractClientTests
{
    private static readonly DateOnly Expiry = new(2026, 9, 4);

    [Fact]
    public async Task FetchesAndValidatesPaginatedExpiredContracts()
    {
        var handler = new PagedHandler(
            Page("next", Contract("one", "SPY260904P00600000", strike: "600")),
            Page(null, Contract("two", "SPY260904P00605000", strike: "605")));
        using var httpClient = new HttpClient(handler);
        var client = new AlpacaOptionContractClient(httpClient, Options());

        OptionContractQuery query = await client.ListAsync(
            "SPY", Expiry, Expiry, "inactive", CancellationToken.None);

        IReadOnlyList<AlpacaOptionContract> contracts = query.Contracts;
        Assert.Equal(2, contracts.Count);
        Assert.Equal(OptionRight.Put, contracts[0].Right);
        Assert.Equal(600m, contracts[0].Strike);
        Assert.Equal(605m, contracts[1].Strike);
        Assert.Equal(OptionExerciseStyle.American, contracts[0].Style);
        Assert.Equal(100, contracts[0].Multiplier);
        Assert.Equal(100, contracts[0].ContractSize);
        Assert.Equal("SPY", contracts[0].RootSymbol);
        Assert.Equal(Expiry, contracts[0].Expiration);
        Assert.False(contracts[0].Tradable);
        Assert.Contains("status=inactive", handler.Requests[0].Query);
        Assert.Contains("page_token=next", handler.Requests[1].Query);
        Assert.Equal(2, query.RequestUris.Count);
        Assert.All(query.RequestUris, uri => Assert.DoesNotContain("test-secret", uri, StringComparison.Ordinal));
        Assert.Equal("SPY", query.Underlying);
        Assert.Equal("inactive", query.Status);
    }

    [Fact]
    public async Task RejectsMismatchedUnderlyingBeforePublishingContract() =>
        await AssertRejectsAsync(Contract("one", "QQQ260904P00600000", strike: "600", underlying: "QQQ", root: "QQQ"));

    [Fact]
    public async Task RejectsStrikePriceThatContradictsTheOccSymbol() =>
        await AssertRejectsAsync(Contract("one", "SPY260904P00600000", strike: "605"));

    [Fact]
    public async Task RejectsTypeThatContradictsTheOccSymbol() =>
        await AssertRejectsAsync(Contract("one", "SPY260904P00600000", strike: "600", type: "call"));

    [Fact]
    public async Task RejectsExpirationDateThatContradictsTheOccSymbol() =>
        await AssertRejectsAsync(Contract("one", "SPY260904P00600000", strike: "600", expiration: "2026-09-11"));

    [Fact]
    public async Task RejectsContractExpiringOutsideTheRequestedWindow() =>
        await AssertRejectsAsync(
            Contract("one", "SPY260911P00600000", strike: "600", expiration: "2026-09-11"));

    [Fact]
    public async Task RejectsStatusThatDoesNotMatchTheRequestedStatus() =>
        await AssertRejectsAsync(Contract("one", "SPY260904P00600000", strike: "600", status: "active"));

    [Fact]
    public async Task RejectsAdjustedContractWhoseRootDiffersFromItsUnderlying() =>
        await AssertRejectsAsync(Contract("one", "SPY260904P00600000", strike: "600", root: "SPY1"));

    [Fact]
    public async Task RejectsNonStandardMultiplierBecauseDefinedRiskWouldBeMiscomputed() =>
        await AssertRejectsAsync(Contract("one", "SPY260904P00600000", strike: "600", multiplier: "10"));

    [Fact]
    public async Task RejectsNonStandardDeliverableSize() =>
        await AssertRejectsAsync(Contract("one", "SPY260904P00600000", strike: "600", size: "50"));

    [Fact]
    public async Task RejectsContractWithoutABrokerIdentifier() =>
        await AssertRejectsAsync(Contract("", "SPY260904P00600000", strike: "600"));

    [Fact]
    public async Task RejectsUnsupportedExerciseStyle() =>
        await AssertRejectsAsync(Contract("one", "SPY260904P00600000", strike: "600", style: "bermudan"));

    [Fact]
    public async Task RejectsConflictingDuplicateDefinitionsOfTheSameContract()
    {
        var handler = new PagedHandler(
            Page("next", Contract("one", "SPY260904P00600000", strike: "600")),
            Page(null, Contract("different-id", "SPY260904P00600000", strike: "600")));
        using var httpClient = new HttpClient(handler);
        var client = new AlpacaOptionContractClient(httpClient, Options());

        await Assert.ThrowsAsync<InvalidDataException>(() => client.ListAsync(
            "SPY", Expiry, Expiry, "inactive", CancellationToken.None));
    }

    [Fact]
    public async Task StopsInsteadOfLoopingWhenAlpacaRepeatsAPageToken()
    {
        var handler = new PagedHandler(Page("same", Contract("one", "SPY260904P00600000", strike: "600")));
        using var httpClient = new HttpClient(handler);
        var client = new AlpacaOptionContractClient(httpClient, Options());

        await Assert.ThrowsAsync<InvalidDataException>(() => client.ListAsync(
            "SPY", Expiry, Expiry, "inactive", CancellationToken.None));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task RejectsInvalidRequestArgumentsBeforeAnyNetworkCall()
    {
        var handler = new PagedHandler(Page(null));
        using var httpClient = new HttpClient(handler);
        var client = new AlpacaOptionContractClient(httpClient, Options());

        await Assert.ThrowsAsync<ArgumentException>(() => client.ListAsync(
            "SPY", Expiry, Expiry, "expired", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => client.ListAsync(
            "SPY", Expiry, Expiry.AddDays(-1), "inactive", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => client.ListAsync(
            "SP Y", Expiry, Expiry, "inactive", CancellationToken.None));

        Assert.Empty(handler.Requests);
    }

    private static async Task AssertRejectsAsync(string contractJson)
    {
        var handler = new PagedHandler(Page(null, contractJson));
        using var httpClient = new HttpClient(handler);
        var client = new AlpacaOptionContractClient(httpClient, Options());

        await Assert.ThrowsAsync<InvalidDataException>(() => client.ListAsync(
            "SPY", Expiry, Expiry, "inactive", CancellationToken.None));
    }

    private static string Page(string? nextPageToken, params string[] contracts)
    {
        string token = nextPageToken is null ? "null" : $"\"{nextPageToken}\"";
        return $$"""{"option_contracts":[{{string.Join(',', contracts)}}],"next_page_token":{{token}}}""";
    }

    private static string Contract(
        string id,
        string symbol,
        string strike,
        string underlying = "SPY",
        string root = "SPY",
        string expiration = "2026-09-04",
        string type = "put",
        string style = "american",
        string multiplier = "100",
        string size = "100",
        string status = "inactive",
        bool tradable = false) =>
        $$"""
        {"id":"{{id}}","symbol":"{{symbol}}","underlying_symbol":"{{underlying}}","root_symbol":"{{root}}",
         "expiration_date":"{{expiration}}","type":"{{type}}","style":"{{style}}","strike_price":"{{strike}}",
         "multiplier":"{{multiplier}}","size":"{{size}}","status":"{{status}}",
         "tradable":{{(tradable ? "true" : "false")}}}
        """;

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
