using System.Net;
using System.Text;
using QuantDesk.Alpaca.Configuration;
using QuantDesk.Alpaca.MarketData;
using QuantDesk.Domain.Options;

namespace QuantDesk.Alpaca.Tests.MarketData;

public sealed class AlpacaOptionContractClientTests
{
    [Fact]
    public async Task FetchesAndValidatesPaginatedExpiredContracts()
    {
        var handler = new PagedHandler(
            """
            {"option_contracts":[{"id":"one","symbol":"SPY260904P00600000",
              "strike_price":"600","status":"inactive","tradable":false}],"next_page_token":"next"}
            """,
            """
            {"option_contracts":[{"id":"two","symbol":"SPY260904P00605000",
              "strike_price":"605","status":"inactive","tradable":false}],"next_page_token":null}
            """);
        using var httpClient = new HttpClient(handler);
        var client = new AlpacaOptionContractClient(httpClient, Options());

        IReadOnlyList<AlpacaOptionContract> contracts = await client.ListAsync(
            "SPY", new DateOnly(2026, 9, 4), new DateOnly(2026, 9, 4),
            "inactive", CancellationToken.None);

        Assert.Equal(2, contracts.Count);
        Assert.Equal(OptionRight.Put, contracts[0].Right);
        Assert.Equal(600m, contracts[0].Strike);
        Assert.Contains("status=inactive", handler.Requests[0].Query);
        Assert.Contains("page_token=next", handler.Requests[1].Query);
    }

    [Fact]
    public async Task RejectsMismatchedUnderlyingBeforePublishingContract()
    {
        var handler = new PagedHandler("""
            {"option_contracts":[{"id":"one","symbol":"QQQ260904P00600000",
              "strike_price":"600","status":"inactive","tradable":false}],"next_page_token":null}
            """);
        using var httpClient = new HttpClient(handler);
        var client = new AlpacaOptionContractClient(httpClient, Options());

        await Assert.ThrowsAsync<InvalidDataException>(() => client.ListAsync(
            "SPY", new DateOnly(2026, 9, 4), new DateOnly(2026, 9, 4),
            "inactive", CancellationToken.None));
    }

    private static AlpacaOptions Options() => new()
    {
        BaseUrl = new Uri("https://paper-api.alpaca.markets"),
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
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(pages[_index++], Encoding.UTF8, "application/json")
            });
        }
    }
}
