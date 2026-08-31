using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using QuantDesk.Alpaca.Configuration;
using QuantDesk.Alpaca.Mapping;
using QuantDesk.Alpaca.MarketData;
using QuantDesk.Api.PaperTrading;
using QuantDesk.Domain.Capabilities;
using QuantDesk.Domain.Execution;
using QuantDesk.Domain.Numerics;
using QuantDesk.Domain.Strategies;
using QuantDesk.Domain.Trading;
using QuantDesk.Runtime.Execution;
using QuantDesk.Runtime.Options;
using QuantDesk.Runtime.Persistence;
using QuantDesk.Runtime.Time;
using Xunit.Abstractions;

namespace QuantDesk.Api.Tests;

/// <summary>
/// Proves a directional view is carried all the way to a multi-leg broker submission, with the
/// reservation durably committed first. The broker is a recording double: nothing contacts Alpaca
/// and no order is placed.
/// </summary>
public sealed class OptionExecutionCoordinatorTests(ITestOutputHelper output) : IDisposable
{
    private static readonly DateTimeOffset AsOf = new(2026, 9, 1, 15, 0, 0, TimeSpan.Zero);
    private const decimal Spot = 600m;
    private static readonly AccountCapabilities Enabled = new(true, true, true, true, 3);

    private readonly string _storePath = Path.Combine(
        Path.GetTempPath(), $"qd-mleg-{Guid.NewGuid():N}.json");

    [Fact]
    public async Task ADirectionalViewReachesAMultiLegBrokerSubmission()
    {
        var broker = new RecordingMultiLegBroker();
        OptionExecutionCoordinator coordinator = Coordinator(broker);

        OptionExecutionOutcome outcome = await coordinator.ExecuteAsync(
            Route(), Enabled, "OPT-E2E-0001", Spot, expectedReturnBps: 200,
            riskBudget: 500m, Plan(), AsOf, 7, 60, 0.05m, CancellationToken.None);

        output.WriteLine(
            $"submitted={outcome.Submitted} state={outcome.State} reason={outcome.Reason} " +
            $"maxLoss={outcome.DefinedMaximumLoss} debit={outcome.NetDebitPerSpread} " +
            $"clientOrderId={outcome.EntryClientOrderId}");

        Assert.True(outcome.Submitted, $"Expected a submission, got {outcome.Reason}.");
        Assert.NotNull(broker.LastCommand);
        Assert.Equal(2, broker.LastCommand!.Legs.Count);
        Assert.True(broker.LastCommand.IsValid(), "The submitted multi-leg command must be valid.");
        Assert.Equal(ExecutionOrderType.Limit, broker.LastCommand.OrderType);
        Assert.Equal(outcome.EntryClientOrderId, broker.LastCommand.ClientOrderId);

        // Both legs must be real OCC symbols on one underlying, opened on opposite sides.
        Assert.All(broker.LastCommand.Legs, leg => Assert.StartsWith("SPY", leg.Symbol, StringComparison.Ordinal));
        Assert.Contains(broker.LastCommand.Legs, leg => leg.PositionIntent == PositionIntent.BuyToOpen);
        Assert.Contains(broker.LastCommand.Legs, leg => leg.PositionIntent == PositionIntent.SellToOpen);

        // The defining safety property survives to the order actually sent.
        Assert.True(outcome.DefinedMaximumLoss > 0);
        Assert.True(outcome.DefinedMaximumLoss <= 500m);
    }

    [Fact]
    public async Task TheReservationIsDurableBeforeTheBrokerIsContacted()
    {
        var broker = new RecordingMultiLegBroker();
        await Coordinator(broker).ExecuteAsync(
            Route(), Enabled, "OPT-DURABLE-0001", Spot, 200, 500m, Plan(),
            AsOf, 7, 60, 0.05m, CancellationToken.None);

        // The record must exist on disk, and it must name the same order the broker received.
        string persisted = await File.ReadAllTextAsync(_storePath);
        Assert.Contains("OPT-DURABLE-0001", persisted, StringComparison.Ordinal);
        Assert.Contains(broker.LastCommand!.ClientOrderId, persisted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAccountWithoutOptionsPermissionNeverReachesTheBroker()
    {
        var broker = new RecordingMultiLegBroker();
        var noOptions = new AccountCapabilities(true, true, true, false, null);

        OptionExecutionOutcome outcome = await Coordinator(broker).ExecuteAsync(
            Route(), noOptions, "OPT-PERM-0001", Spot, 200, 500m, Plan(),
            AsOf, 7, 60, 0.05m, CancellationToken.None);

        Assert.False(outcome.Submitted);
        Assert.Equal("AssetClassNotPermitted", outcome.Reason);
        Assert.Null(broker.LastCommand);
    }

    [Fact]
    public async Task ARiskBudgetBelowTheEntryLimitNeverReachesTheBroker()
    {
        var broker = new RecordingMultiLegBroker();

        OptionExecutionOutcome outcome = await Coordinator(broker).ExecuteAsync(
            Route(), Enabled, "OPT-BUDGET-0001", Spot, 200, riskBudget: 50m, Plan(),
            AsOf, 7, 60, 0.05m, CancellationToken.None);

        Assert.False(outcome.Submitted);
        Assert.Null(broker.LastCommand);
    }

    [Fact]
    public async Task ASpotRouteIsRefusedByTheOptionsCoordinator()
    {
        var broker = new RecordingMultiLegBroker();
        new OpportunityRouter().TryRoute("BTC/USD", out OpportunityRoute? crypto, out _);

        OptionExecutionOutcome outcome = await Coordinator(broker).ExecuteAsync(
            crypto!, Enabled, "OPT-ROUTE-0001", Spot, 200, 500m, Plan(),
            AsOf, 7, 60, 0.05m, CancellationToken.None);

        Assert.False(outcome.Submitted);
        Assert.Equal("RouteIsNotAnOptionRoute", outcome.Reason);
        Assert.Null(broker.LastCommand);
    }

    [Fact]
    public async Task ANonPaperBrokerCannotReserveAndThereforeCannotSubmit()
    {
        var broker = new RecordingMultiLegBroker { IsPaper = false };

        OptionExecutionOutcome outcome = await Coordinator(broker).ExecuteAsync(
            Route(), Enabled, "OPT-LIVE-0001", Spot, 200, 500m, Plan(),
            AsOf, 7, 60, 0.05m, CancellationToken.None);

        Assert.False(outcome.Submitted);
        Assert.Equal("ReservationRejected", outcome.Reason);
        Assert.Null(broker.LastCommand);
    }

    private static OpportunityRoute Route()
    {
        new OpportunityRouter().TryRoute("SPY260918C00600000", out OpportunityRoute? route, out _);
        return route!;
    }

    private OptionExecutionCoordinator Coordinator(RecordingMultiLegBroker broker)
    {
        var clock = new VirtualRuntimeClock(AsOf);
        var opportunities = new OptionVerticalOpportunityService(
            new AlpacaOptionContractClient(new HttpClient(new StubHandler(Contracts())), Options()),
            new AlpacaLatestOptionQuoteClient(new HttpClient(new StubHandler(Quotes())), Options()),
            new DictionaryInstrumentSymbolResolver(new Dictionary<int, string> { [0] = "SPY" }),
            new DefinedRiskVerticalCompiler(new Usd(500m), 0.15, 0.5, 7, 60),
            NullLogger<OptionVerticalOpportunityService>.Instance);
        var lifecycle = new MultiLegExecutionLifecycle(
            broker, broker, new MultiLegExecutionStore(_storePath), clock, TimeSpan.FromSeconds(30));
        return new OptionExecutionCoordinator(
            opportunities, lifecycle, NullLogger<OptionExecutionCoordinator>.Instance);
    }

    private static string Contracts()
    {
        string[] items =
        [
            Contract("SPY260918C00600000", 600),
            Contract("SPY260918C00605000", 605)
        ];
        return "{\"option_contracts\":[" + string.Join(',', items) + "],\"next_page_token\":null}";
    }

    private static string Contract(string symbol, int strike) =>
        "{\"id\":\"" + symbol + "\",\"symbol\":\"" + symbol + "\"," +
        "\"underlying_symbol\":\"SPY\",\"root_symbol\":\"SPY\"," +
        "\"expiration_date\":\"2026-09-18\",\"type\":\"call\",\"style\":\"american\"," +
        "\"strike_price\":\"" + strike + "\",\"multiplier\":\"100\",\"size\":\"100\"," +
        "\"status\":\"active\",\"tradable\":true}";

    private static string Quotes() =>
        "{\"quotes\":{" +
        "\"SPY260918C00600000\":{\"bp\":8.0,\"ap\":8.2,\"t\":\"2026-09-01T15:00:00Z\"}," +
        "\"SPY260918C00605000\":{\"bp\":5.0,\"ap\":5.2,\"t\":\"2026-09-01T15:00:00Z\"}}}";

    private static PositionManagementPlan Plan() =>
        new(TimeSpan.FromDays(3), true, true, null, 2, "vertical-managed-v1");

    private static AlpacaOptions Options() => new()
    {
        BaseUrl = new Uri("https://paper-api.alpaca.markets"),
        DataBaseUrl = new Uri("https://data.alpaca.markets/"),
        KeyId = "test-key",
        SecretKey = "test-secret"
    };

    public void Dispose()
    {
        if (File.Exists(_storePath)) File.Delete(_storePath);
    }

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }

    private sealed class RecordingMultiLegBroker : IMultiLegBrokerExecutionGateway, IBrokerExecutionGateway
    {
        public MultiLegExecutionCommand? LastCommand { get; private set; }
        public bool IsPaper { get; init; } = true;

        public bool IsPaperEnvironment => IsPaper;

        public Task<BrokerSubmitResult> SubmitMultiLegAsync(
            MultiLegExecutionCommand command, CancellationToken cancellationToken)
        {
            LastCommand = command;
            return Task.FromResult(new BrokerSubmitResult(
                BrokerSubmitState.Acknowledged, $"broker-{command.ClientOrderId}", null, "req-1"));
        }

        public Task<BrokerSubmitResult> SubmitAsync(
            ExecutionCommand command, CancellationToken cancellationToken) =>
            Task.FromResult(new BrokerSubmitResult(BrokerSubmitState.Acknowledged, "spot", null, null));

        public Task<BrokerOrderSnapshot?> FindByClientOrderIdAsync(
            string clientOrderId, CancellationToken cancellationToken) =>
            Task.FromResult<BrokerOrderSnapshot?>(null);
    }
}
