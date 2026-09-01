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
        Assert.Empty(query.Excluded);
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
    public async Task RejectsContractWithoutABrokerIdentifier() =>
        await AssertRejectsAsync(Contract("", "SPY260904P00600000", strike: "600"));

    [Theory]
    [InlineData("root", "SPY1", "adjusted or non-standard")]
    [InlineData("multiplier", "10", "non-standard multiplier")]
    [InlineData("size", "50", "non-standard deliverable size")]
    [InlineData("style", "bermudan", "unsupported exercise style")]
    public async Task ExcludesAContractThisSystemCannotPriceAndSaysWhy(
        string field, string value, string expectedReason)
    {
        OptionContractQuery query = await ListAsync(NonStandard(field, value));

        Assert.Empty(query.Contracts);
        OptionContractExclusion exclusion = Assert.Single(query.Excluded);
        Assert.Equal("SPY260904P00600000", exclusion.Symbol);
        Assert.Contains(expectedReason, exclusion.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OneAdjustedContractDoesNotCostTheTradableOnesInTheSameChain()
    {
        // The reason exclusion exists. Failing the query over a single adjusted contract would have
        // thrown away every standard contract beside it, which on a real chain is nearly all of them.
        OptionContractQuery query = await ListAsync(
            Contract("one", "SPY260904P00600000", strike: "600", root: "SPY1"),
            Contract("two", "SPY260904P00605000", strike: "605"),
            Contract("three", "SPY260904P00610000", strike: "610"));

        Assert.Equal(2, query.Contracts.Count);
        Assert.Equal([605m, 610m], query.Contracts.Select(contract => contract.Strike));
        Assert.Equal("SPY260904P00600000", Assert.Single(query.Excluded).Symbol);
    }

    [Fact]
    public async Task AVenueRefusalReportsTheStatusAndTheVenuesOwnExplanation()
    {
        // What an operator sees on the first live call with an unentitled account. Before, this was
        // "Response status code does not indicate success: 403 (Forbidden)" and nothing else.
        var handler = new PagedHandler(HttpStatusCode.Forbidden,
            """{"code":40110000,"message":"account is not authorized to trade options"}""");
        using var httpClient = new HttpClient(handler);
        var client = new AlpacaOptionContractClient(httpClient, Options());

        HttpRequestException failure = await Assert.ThrowsAsync<HttpRequestException>(() => client.ListAsync(
            "SPY", Expiry, Expiry, "inactive", CancellationToken.None));

        Assert.Equal(HttpStatusCode.Forbidden, failure.StatusCode);
        Assert.Contains("/v2/options/contracts", failure.Message, StringComparison.Ordinal);
        Assert.Contains("403", failure.Message, StringComparison.Ordinal);
        Assert.Contains("code 40110000", failure.Message, StringComparison.Ordinal);
        Assert.Contains("not authorized to trade options", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANonJsonRefusalStillReportsABoundedExcerptRatherThanNothing()
    {
        var handler = new PagedHandler(
            HttpStatusCode.BadGateway, "<html><body>\n  502 Bad Gateway\n</body></html>");
        using var httpClient = new HttpClient(handler);
        var client = new AlpacaOptionContractClient(httpClient, Options());

        HttpRequestException failure = await Assert.ThrowsAsync<HttpRequestException>(() => client.ListAsync(
            "SPY", Expiry, Expiry, "inactive", CancellationToken.None));

        Assert.Contains("502", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Bad Gateway", failure.Message, StringComparison.Ordinal);
    }

    private static string NonStandard(string field, string value) => field switch
    {
        "root" => Contract("one", "SPY260904P00600000", strike: "600", root: value),
        "multiplier" => Contract("one", "SPY260904P00600000", strike: "600", multiplier: value),
        "size" => Contract("one", "SPY260904P00600000", strike: "600", size: value),
        "style" => Contract("one", "SPY260904P00600000", strike: "600", style: value),
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Unhandled contract field.")
    };

    private static async Task<OptionContractQuery> ListAsync(params string[] contracts)
    {
        using var httpClient = new HttpClient(new PagedHandler(Page(null, contracts)));
        var client = new AlpacaOptionContractClient(httpClient, Options());
        return await client.ListAsync("SPY", Expiry, Expiry, "inactive", CancellationToken.None);
    }

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
