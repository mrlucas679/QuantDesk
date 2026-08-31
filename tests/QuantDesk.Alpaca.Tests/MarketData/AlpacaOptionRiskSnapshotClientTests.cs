using System.Net;
using System.Text;
using QuantDesk.Alpaca.Configuration;
using QuantDesk.Alpaca.MarketData;
using QuantDesk.Domain.Runtime;

namespace QuantDesk.Alpaca.Tests.MarketData;

public sealed class AlpacaOptionRiskSnapshotClientTests
{
    [Fact]
    public async Task AuthenticatedSnapshotGreeksAreMappedOnlyWhenFreshAndComplete()
    {
        using var http = new HttpClient(new StubHandler("""
            {"snapshots":{"SPY260918C00600000":{"latestQuote":{"t":"2026-08-31T15:00:00Z"},"greeks":{"implied_volatility":0.2,"delta":0.5,"gamma":0.01,"vega":0.12,"theta":-0.03}}}}
            """));
        var client = new AlpacaOptionRiskSnapshotClient(http, Options());

        IReadOnlyDictionary<int, QuantDesk.Domain.Options.OptionRiskSnapshot> snapshots = await client.GetSnapshotsAsync(
            new Dictionary<string, int> { ["SPY260918C00600000"] = 42 },
            DateTimeOffset.Parse("2026-08-31T15:00:30Z"), TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.Equal(DataQuality.Healthy, snapshots[42].Quality);
        Assert.Equal(.5, snapshots[42].Delta);
        Assert.Equal(.01, snapshots[42].Gamma);
    }

    [Fact]
    public async Task MissingGreeksRemainExplicitlyStale()
    {
        using var http = new HttpClient(new StubHandler("""
            {"snapshots":{"SPY260918C00600000":{"latestQuote":{"t":"2026-08-31T15:00:00Z"}}}}
            """));
        var client = new AlpacaOptionRiskSnapshotClient(http, Options());

        IReadOnlyDictionary<int, QuantDesk.Domain.Options.OptionRiskSnapshot> snapshots = await client.GetSnapshotsAsync(
            new Dictionary<string, int> { ["SPY260918C00600000"] = 42 },
            DateTimeOffset.Parse("2026-08-31T15:00:30Z"), TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.Equal(DataQuality.Stale, snapshots[42].Quality);
    }

    private static AlpacaOptions Options() => new()
    {
        BaseUrl = new Uri("https://paper-api.alpaca.markets"),
        DataBaseUrl = new Uri("https://data.alpaca.markets/"), KeyId = "test", SecretKey = "test"
    };

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }
}
