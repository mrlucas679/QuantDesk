using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using QuantDesk.Alpaca.Configuration;
using QuantDesk.Alpaca.Mapping;
using QuantDesk.Alpaca.MarketData;
using QuantDesk.Api.PaperTrading;
using QuantDesk.Domain.Numerics;
using QuantDesk.Domain.Strategies;
using QuantDesk.Runtime.Options;
using Xunit.Abstractions;

namespace QuantDesk.Api.Tests;

/// <summary>
/// Drives a directional view on SPY through contract discovery, live option pricing, and
/// defined-risk compilation, using stubbed HTTP so no order and no broker call occurs.
/// </summary>
public sealed class OptionVerticalOpportunityServiceTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset AsOf = new(2026, 8, 31, 15, 0, 0, TimeSpan.Zero);
    private const decimal Spot = 600m;

    [Fact]
    public async Task ABullishViewProducesAPricedSpreadWhoseWorstCaseIsCapped()
    {
        OptionOpportunityOutcome outcome = await Service(riskBudget: 400m).FindAsync(
            "SPY", Spot, expectedReturnBps: 200, candidateId: 7, costBps: 32m,
            Plan(), AsOf, 7, 60, 0.05m, CancellationToken.None);

        output.WriteLine(
            $"considered={outcome.ContractsConsidered} priced={outcome.ContractsPriced} " +
            $"reason={outcome.Reason} maxLoss={outcome.Compilation?.DefinedMaximumLoss.Value} " +
            $"maxProfit={outcome.Compilation?.MaximumProfit.Value}");

        Assert.True(outcome.Admitted, $"Expected an admitted spread, got {outcome.Reason}.");
        Assert.Equal("spy-bull-call-vertical-v1", outcome.Compilation!.Candidate!.StrategyId);
        Assert.Equal(2, outcome.Compilation.Candidate.Legs.Count);

        // The defining safety property: the debit paid is the entire downside, and it is capped by
        // the configured budget before the order can exist.
        Assert.True(outcome.Compilation.DefinedMaximumLoss.Value > 0);
        Assert.True(outcome.Compilation.DefinedMaximumLoss.Value <= 400m);
        Assert.Equal(
            outcome.Compilation.DefinedMaximumLoss,
            outcome.Compilation.Candidate.DefinedMaximumLoss);
    }

    [Fact]
    public async Task ARiskBudgetSmallerThanTheDebitAdmitsNothing()
    {
        OptionOpportunityOutcome outcome = await Service(riskBudget: 5m).FindAsync(
            "SPY", Spot, 200, 7, 32m, Plan(), AsOf, 7, 60, 0.05m, CancellationToken.None);

        Assert.False(outcome.Admitted);
        Assert.Equal(nameof(VerticalRejection.DebitExceedsRiskBudget), outcome.Reason);
    }

    [Fact]
    public async Task StaleOptionQuotesRefuseTheSpreadRatherThanPricingFromThem()
    {
        // Quotes timestamped an hour before the decision are beyond the freshness window.
        OptionOpportunityOutcome outcome = await Service(quoteTimestamp: "2026-08-31T14:00:00Z")
            .FindAsync("SPY", Spot, 200, 7, 32m, Plan(), AsOf, 7, 60, 0.05m, CancellationToken.None);

        Assert.False(outcome.Admitted);
        Assert.Equal(nameof(VerticalRejection.QuoteUnhealthy), outcome.Reason);
    }

    [Fact]
    public async Task NoDirectionalConvictionNeverReachesTheChain()
    {
        OptionOpportunityOutcome outcome = await Service().FindAsync(
            "SPY", Spot, expectedReturnBps: 0, candidateId: 7, costBps: 32m,
            Plan(), AsOf, 7, 60, 0.05m, CancellationToken.None);

        Assert.False(outcome.Admitted);
        Assert.Equal("NoDirectionalConviction", outcome.Reason);
        Assert.Equal(0, outcome.ContractsConsidered);
    }

    [Fact]
    public async Task AStrikeBandThatCoversNothingIsReportedDistinctly()
    {
        OptionOpportunityOutcome outcome = await Service().FindAsync(
            "SPY", Spot, 200, 7, 32m, Plan(), AsOf, 7, 60,
            strikeBandFraction: 0.0001m, CancellationToken.None);

        Assert.False(outcome.Admitted);
        Assert.Equal("NoStrikeBandCoverage", outcome.Reason);
    }

    private static OptionVerticalOpportunityService Service(
        decimal riskBudget = 400m,
        string quoteTimestamp = "2026-08-31T15:00:00Z")
    {
        var contractHandler = new StubHandler(ContractsPayload());
        var quoteHandler = new StubHandler(QuotesPayload(quoteTimestamp));
        var resolver = new DictionaryInstrumentSymbolResolver(new Dictionary<int, string> { [0] = "SPY" });
        return new OptionVerticalOpportunityService(
            new AlpacaOptionContractClient(new HttpClient(contractHandler), Options()),
            new AlpacaLatestOptionQuoteClient(new HttpClient(quoteHandler), Options()),
            resolver,
            new DefinedRiskVerticalCompiler(new Usd(riskBudget), 0.15, 0.5, 7, 60),
            NullLogger<OptionVerticalOpportunityService>.Instance);
    }

    private static string ContractsPayload()
    {
        string[] contracts =
        [
            Contract("SPY260918C00600000", 600),
            Contract("SPY260918C00605000", 605)
        ];
        return "{\"option_contracts\":[" + string.Join(',', contracts) + "],\"next_page_token\":null}";
    }

    private static string Contract(string symbol, int strike) =>
        "{\"id\":\"" + symbol + "\",\"symbol\":\"" + symbol + "\"," +
        "\"underlying_symbol\":\"SPY\",\"root_symbol\":\"SPY\"," +
        "\"expiration_date\":\"2026-09-18\",\"type\":\"call\",\"style\":\"american\"," +
        "\"strike_price\":\"" + strike + "\",\"multiplier\":\"100\",\"size\":\"100\"," +
        "\"status\":\"active\",\"tradable\":true}";

    private static string QuotesPayload(string timestamp) =>
        "{\"quotes\":{" +
        "\"SPY260918C00600000\":{\"bp\":8.0,\"ap\":8.2,\"t\":\"" + timestamp + "\"}," +
        "\"SPY260918C00605000\":{\"bp\":5.0,\"ap\":5.2,\"t\":\"" + timestamp + "\"}}}";

    private static PositionManagementPlan Plan() =>
        new(TimeSpan.FromDays(5), true, true, null, 2, "vertical-managed-v1");

    private static AlpacaOptions Options() => new()
    {
        BaseUrl = new Uri("https://paper-api.alpaca.markets"),
        DataBaseUrl = new Uri("https://data.alpaca.markets/"),
        KeyId = "test-key",
        SecretKey = "test-secret"
    };

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }
}
