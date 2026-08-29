using System.Net;
using System.Text;
using QuantDesk.Alpaca.Configuration;
using QuantDesk.Alpaca.Mapping;
using QuantDesk.Alpaca.Trading;
using QuantDesk.Domain.Execution;
using QuantDesk.Domain.Trading;

namespace QuantDesk.Alpaca.Tests.Trading;

public sealed class AlpacaTradingGatewayTests
{
    [Fact]
    public async Task GetAccountAsync_MapsPaperExecutionPreflight()
    {
        var handler = new CaptureHandler(HttpStatusCode.OK, """
            {"id":"account-1","status":"ACTIVE","equity":"100000.50","buying_power":"200000",
             "trading_blocked":false,"account_blocked":false}
            """);
        using var client = new HttpClient(handler);
        var gateway = new AlpacaTradingGateway(client, Options(), Resolver());

        BrokerAccountSnapshot? account = await gateway.GetAccountAsync(CancellationToken.None);

        Assert.NotNull(account);
        Assert.Equal("account-1", account.AccountId);
        Assert.Equal(100000.50m, account.Equity);
        Assert.Equal(HttpMethod.Get, handler.Request!.Method);
        Assert.Equal("/v2/account", handler.Request.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task SubmitAsync_MapsPaperOrderAndPreservesRequestId()
    {
        var handler = new CaptureHandler(HttpStatusCode.OK, """
            {"id":"broker-1","client_order_id":"qd-1","status":"new"}
            """);
        using var client = new HttpClient(handler);
        var gateway = new AlpacaTradingGateway(client, Options(), Resolver());

        BrokerSubmitResult result = await gateway.SubmitAsync(Command(), CancellationToken.None);

        Assert.Equal(BrokerSubmitState.Acknowledged, result.State);
        Assert.Equal("broker-1", result.BrokerOrderId);
        Assert.Equal("request-1", result.RequestId);
        HttpRequestMessage capturedRequest = handler.Request ?? throw new InvalidOperationException("Request was not captured.");
        Assert.Contains("/v2/orders", capturedRequest.RequestUri!.AbsolutePath);
        string body = handler.RequestBody;
        Assert.Contains("\"symbol\":\"SPY\"", body);
        Assert.Contains("\"client_order_id\":\"qd-1\"", body);
    }

    [Fact]
    public async Task SubmitAsync_UnknownInstrumentDoesNotCallProvider()
    {
        var handler = new CaptureHandler(HttpStatusCode.OK, "{}");
        using var client = new HttpClient(handler);
        var gateway = new AlpacaTradingGateway(client, Options(), Resolver());
        ExecutionCommand command = Command() with { InstrumentSlot = 99 };

        BrokerSubmitResult result = await gateway.SubmitAsync(command, CancellationToken.None);

        Assert.Equal(BrokerSubmitState.Rejected, result.State);
        Assert.Equal("UNKNOWN_INSTRUMENT_SLOT", result.ReasonCode);
        Assert.Null(handler.Request);
    }

    [Fact]
    public async Task CancelAsync_UsesDeleteAndPreservesOrderIdentity()
    {
        var handler = new CaptureHandler(HttpStatusCode.NoContent, string.Empty);
        using var client = new HttpClient(handler);
        var gateway = new AlpacaTradingGateway(client, Options(), Resolver());

        BrokerSubmitResult result = await gateway.CancelAsync("broker/1", CancellationToken.None);

        Assert.Equal(BrokerSubmitState.Acknowledged, result.State);
        Assert.Equal("broker/1", result.BrokerOrderId);
        Assert.Equal(HttpMethod.Delete, handler.Request!.Method);
        Assert.Contains("broker%2F1", handler.Request.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task ReplaceAsync_RejectsEmptyReplacementBeforeCallingProvider()
    {
        var handler = new CaptureHandler(HttpStatusCode.OK, "{}");
        using var client = new HttpClient(handler);
        var gateway = new AlpacaTradingGateway(client, Options(), Resolver());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            gateway.ReplaceAsync("broker-1", null, null, CancellationToken.None));

        Assert.Null(handler.Request);
    }

    [Fact]
    public async Task ClosePositionAsync_NormalizesCryptoSymbolForBrokerRoute()
    {
        var handler = new CaptureHandler(HttpStatusCode.OK, """
            {"id":"close-1","client_order_id":"broker-close","status":"pending_new"}
            """);
        using var client = new HttpClient(handler);
        var resolver = new DictionaryInstrumentSymbolResolver(
            new Dictionary<int, string> { [0] = "BTC/USD" });
        var gateway = new AlpacaTradingGateway(client, Options(), resolver);

        BrokerSubmitResult result = await gateway.ClosePositionAsync(0, CancellationToken.None);

        Assert.Equal(BrokerSubmitState.Acknowledged, result.State);
        Assert.Equal("close-1", result.BrokerOrderId);
        Assert.Equal("/v2/positions/BTCUSD", handler.Request!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public void Resolver_MatchesBrokerNormalizedCryptoSymbol()
    {
        var resolver = new DictionaryInstrumentSymbolResolver(
            new Dictionary<int, string> { [4] = "BTC/USD" });

        Assert.True(resolver.TryResolveBySymbol("BTCUSD", out int slot));
        Assert.Equal(4, slot);
    }

    private static AlpacaOptions Options() => new()
    {
        BaseUrl = new Uri("https://paper-api.alpaca.markets"),
        KeyId = "test-key",
        SecretKey = "test-secret"
    };

    private static IInstrumentSymbolResolver Resolver() =>
        new DictionaryInstrumentSymbolResolver(new Dictionary<int, string> { [0] = "SPY" });

    private static ExecutionCommand Command() => new(
        1, ExecutionPriority.ExploitationEntry, 1, 1, "qd-1", 0, OrderSide.Buy,
        PositionIntent.Open, ExecutionOrderType.Limit, ExecutionTimeInForce.Day,
        1, 100, 10, 100, "trend");

    private sealed class CaptureHandler(HttpStatusCode statusCode, string responseBody) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        public string RequestBody { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            RequestBody = request.Content is null ? string.Empty : request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
            response.Headers.Add("X-Request-ID", "request-1");
            return Task.FromResult(response);
        }
    }
}
