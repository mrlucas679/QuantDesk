using System.Net;
using System.Text;
using QuantDesk.Alpaca.Capabilities;
using QuantDesk.Alpaca.Configuration;

namespace QuantDesk.Alpaca.Tests.Capabilities;

public sealed class AlpacaCapabilityProbeTests
{
    [Fact]
    public async Task ProbeAsync_MapsVerifiedAccountCapabilities()
    {
        const string body = """
            {
              "status": "ACTIVE",
              "trading_blocked": false,
              "account_blocked": false,
              "options_trading_level": 3,
              "crypto_status": "ACTIVE"
            }
            """;
        using var client = new HttpClient(new StubHttpMessageHandler(HttpStatusCode.OK, body));
        var probe = new AlpacaCapabilityProbe(client, PaperOptions());

        var report = await probe.ProbeAsync(CancellationToken.None);

        Assert.True(report.PaperEnvironment);
        Assert.True(report.EquityTrading);
        Assert.True(report.CryptoTrading);
        Assert.True(report.OptionsTrading);
        Assert.Equal(3, report.OptionsTradingLevel);
        Assert.False(report.TradeUpdateStream);
        Assert.Equal("request-123", report.RequestId);
    }

    [Fact]
    public async Task ProbeAsync_FailsClosedWhenProviderRejectsRequest()
    {
        using var client = new HttpClient(new StubHttpMessageHandler(HttpStatusCode.Unauthorized, "{}"));
        var probe = new AlpacaCapabilityProbe(client, PaperOptions());

        var report = await probe.ProbeAsync(CancellationToken.None);

        Assert.False(report.EquityTrading);
        Assert.False(report.OptionsTrading);
        Assert.Single(report.Problems);
    }

    private static AlpacaOptions PaperOptions() => new()
    {
        BaseUrl = new Uri("https://paper-api.alpaca.markets"),
        KeyId = "test-key",
        SecretKey = "test-secret"
    };

    private sealed class StubHttpMessageHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal("test-key", request.Headers.GetValues("APCA-API-KEY-ID").Single());
            Assert.Equal("test-secret", request.Headers.GetValues("APCA-API-SECRET-KEY").Single());

            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            response.Headers.Add("X-Request-ID", "request-123");
            return Task.FromResult(response);
        }
    }
}

