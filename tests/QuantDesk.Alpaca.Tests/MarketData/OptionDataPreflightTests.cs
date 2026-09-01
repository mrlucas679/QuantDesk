using System.Net;
using System.Text;
using QuantDesk.Alpaca.Configuration;
using QuantDesk.Alpaca.MarketData;

namespace QuantDesk.Alpaca.Tests.MarketData;

/// <summary>
/// The preflight is what turns first contact with the venue into a report instead of a stack trace,
/// so what is tested here is mostly its behaviour when things go wrong.
/// </summary>
public sealed class OptionDataPreflightTests
{
    private static readonly DateTimeOffset AsOf = DateTimeOffset.Parse("2026-09-04T15:00:00Z");
    private static readonly DateOnly Start = new(2026, 9, 4);
    private static readonly DateOnly End = new(2026, 10, 19);

    [Fact]
    public async Task AHealthyVenueReportsEveryStagePassed()
    {
        OptionPreflightReport report = await RunAsync(new RoutingHandler());

        Assert.True(report.Passed);
        Assert.Equal(
            ["contract discovery", "latest quotes", "greeks and implied volatility", "historical bars"],
            report.Steps.Select(step => step.Name));
        Assert.All(report.Steps, step => Assert.Equal(OptionPreflightOutcome.Passed, step.Outcome));
    }

    [Fact]
    public async Task OneRefusedEndpointDoesNotHideTheOthers()
    {
        // The property worth having. Learning that contracts resolve but quotes are unentitled is a
        // different situation from learning that nothing works, and one run has to distinguish them.
        var handler = new RoutingHandler { QuoteStatus = HttpStatusCode.Forbidden };

        OptionPreflightReport report = await RunAsync(handler);

        Assert.False(report.Passed);
        Assert.Equal(OptionPreflightOutcome.Passed, Step(report, "contract discovery").Outcome);
        Assert.Equal(OptionPreflightOutcome.Failed, Step(report, "latest quotes").Outcome);
        Assert.Contains("403", Step(report, "latest quotes").Detail, StringComparison.Ordinal);
        Assert.Equal(OptionPreflightOutcome.Passed, Step(report, "greeks and implied volatility").Outcome);
        Assert.Equal(OptionPreflightOutcome.Passed, Step(report, "historical bars").Outcome);
    }

    [Fact]
    public async Task ARefusalCarriesTheVenuesOwnExplanationIntoTheReport()
    {
        var handler = new RoutingHandler
        {
            ContractStatus = HttpStatusCode.Forbidden,
            ContractBody = """{"code":40110000,"message":"account is not authorized to trade options"}"""
        };

        OptionPreflightReport report = await RunAsync(handler);

        Assert.Contains("not authorized to trade options",
            Step(report, "contract discovery").Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutContractsThePricingStagesAreSkippedRatherThanReportedAsFailures()
    {
        // Skipped and Failed mean different things: nothing was learned about quotes here, and saying
        // they failed would invent a second problem out of the first.
        var handler = new RoutingHandler { ContractStatus = HttpStatusCode.Forbidden };

        OptionPreflightReport report = await RunAsync(handler);

        Assert.Equal(OptionPreflightOutcome.Failed, Step(report, "contract discovery").Outcome);
        foreach (string name in new[] { "latest quotes", "greeks and implied volatility", "historical bars" })
            Assert.Equal(OptionPreflightOutcome.Skipped, Step(report, name).Outcome);
    }

    [Fact]
    public async Task ExcludedContractsAreReportedWithoutFailingAChainThatStillHasTradableOnes()
    {
        // An adjusted contract in a chain is normal. Reporting it is the point; failing over it is not.
        var handler = new RoutingHandler { IncludeAdjustedContract = true };

        OptionPreflightReport report = await RunAsync(handler);

        OptionPreflightStep discovery = Step(report, "contract discovery");
        Assert.Equal(OptionPreflightOutcome.Passed, discovery.Outcome);
        Assert.Contains("1 excluded", discovery.Detail, StringComparison.Ordinal);
        Assert.Contains("adjusted or non-standard", discovery.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AChainThatIsEntirelyExcludedIsAFailureAndSaysSo()
    {
        // The case the count exists to expose: the venue answered, and nothing in the answer is usable.
        var handler = new RoutingHandler { OnlyAdjustedContracts = true };

        OptionPreflightReport report = await RunAsync(handler);

        Assert.False(report.Passed);
        Assert.Equal(OptionPreflightOutcome.Failed, Step(report, "contract discovery").Outcome);
        Assert.Contains("0 published", Step(report, "contract discovery").Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QuotesThatAreAllStaleFailButReportTheirAge()
    {
        // A successful HTTP call that prices nothing cannot compile a defined-risk debit — but the
        // report has to say how stale, or a reader cannot tell a dead feed from an idle one.
        var handler = new RoutingHandler { StaleQuotes = true };

        OptionPreflightReport report = await RunAsync(handler);

        OptionPreflightStep quotes = Step(report, "latest quotes");
        Assert.Equal(OptionPreflightOutcome.Failed, quotes.Outcome);
        Assert.Contains("1 quoted but all older than the 15-minute limit",
            quotes.Detail, StringComparison.Ordinal);
        Assert.Contains("6.0 hours old", quotes.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DuringTheSessionStaleQuotesAreCalledARealProblem()
    {
        // AsOf is 15:00Z on a Friday, inside 13:30-20:00Z.
        var handler = new RoutingHandler { StaleQuotes = true };

        OptionPreflightReport report = await RunAsync(handler);

        Assert.Contains("market is open, so stale quotes here are a real problem",
            Step(report, "latest quotes").Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OutsideTheSessionStaleQuotesAreExplainedRatherThanBlamed()
    {
        // The failure this fixes: run before the open and every quote is stale by design, which the
        // report used to present as a fault. Same payload, six hours earlier.
        // The quote must still be stale at the earlier asOf, or this would test a fresh quote.
        var handler = new RoutingHandler
        {
            StaleQuotes = true,
            StaleQuoteTimestamp = "2026-09-03T20:00:00Z"
        };
        OptionPreflightReport report = await RunAsync(handler, asOf: DateTimeOffset.Parse("2026-09-04T09:00:00Z"));

        string detail = Step(report, "latest quotes").Detail;
        Assert.Contains("market is closed", detail, StringComparison.Ordinal);
        Assert.Contains("Re-run during the session", detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AVenueThatQuotesNothingIsDistinguishedFromOneThatQuotesStaleData()
    {
        var handler = new RoutingHandler { NoQuotes = true };

        OptionPreflightReport report = await RunAsync(handler);

        Assert.Contains("returned no quote for any sampled contract",
            Step(report, "latest quotes").Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoRequestIsEverAWriteBecauseThePreflightMustNotTrade()
    {
        var handler = new RoutingHandler();

        await RunAsync(handler);

        Assert.NotEmpty(handler.Methods);
        Assert.All(handler.Methods, method => Assert.Equal(HttpMethod.Get, method));
    }

    private static OptionPreflightStep Step(OptionPreflightReport report, string name) =>
        report.Steps.Single(step => step.Name == name);

    private static async Task<OptionPreflightReport> RunAsync(
        RoutingHandler handler, DateTimeOffset? asOf = null)
    {
        using var httpClient = new HttpClient(handler);
        AlpacaOptions options = Options();
        var preflight = new OptionDataPreflight(
            new AlpacaOptionContractClient(httpClient, options),
            new AlpacaLatestOptionQuoteClient(httpClient, options),
            new AlpacaOptionRiskSnapshotClient(httpClient, options),
            // The bar client holds its window behind Alpaca's real-time boundary using its own clock,
            // so the test has to own that clock too or the two disagree about "now".
            new AlpacaHistoricalOptionBarClient(httpClient, options, new FixedTime(asOf ?? AsOf)));
        return await preflight.RunAsync("SPY", Start, End, asOf ?? AsOf, CancellationToken.None);
    }

    private static AlpacaOptions Options() => new()
    {
        BaseUrl = new Uri("https://paper-api.alpaca.markets"),
        DataBaseUrl = new Uri("https://data.alpaca.markets/"),
        KeyId = "test-key",
        SecretKey = "test-secret"
    };

    /// <summary>
    /// Answers each option endpoint with a venue-shaped payload, and lets one test at a time make a
    /// single endpoint misbehave.
    /// </summary>
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RoutingHandler : HttpMessageHandler
    {
        private const string Symbol = "SPY261016C00600000";
        private const string Adjusted = "SPY1261016C00600000";

        public HttpStatusCode ContractStatus { get; set; } = HttpStatusCode.OK;
        public HttpStatusCode QuoteStatus { get; set; } = HttpStatusCode.OK;
        public string? ContractBody { get; set; }
        public bool IncludeAdjustedContract { get; set; }
        public bool OnlyAdjustedContracts { get; set; }
        public bool StaleQuotes { get; set; }
        public bool NoQuotes { get; set; }
        public string StaleQuoteTimestamp { get; set; } = "2026-09-04T09:00:00Z";
        public List<HttpMethod> Methods { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Methods.Add(request.Method);
            string path = request.RequestUri!.AbsolutePath;
            (HttpStatusCode status, string body) = path switch
            {
                "/v2/options/contracts" => (ContractStatus, ContractBody ?? Contracts()),
                "/v1beta1/options/quotes/latest" => (QuoteStatus, Quotes()),
                "/v1beta1/options/snapshots" => (HttpStatusCode.OK, Snapshots()),
                "/v1beta1/options/bars" => (HttpStatusCode.OK, Bars()),
                _ => (HttpStatusCode.NotFound, """{"message":"unrouted path in test"}""")
            };

            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }

        private string Contracts()
        {
            List<string> entries = [];
            if (!OnlyAdjustedContracts) entries.Add(Contract(Symbol, "SPY"));
            if (IncludeAdjustedContract || OnlyAdjustedContracts) entries.Add(Contract(Adjusted, "SPY1"));
            return "{\"option_contracts\":[" + string.Join(',', entries) + "],\"next_page_token\":null}";
        }

        private static string Contract(string symbol, string root) =>
            "{\"id\":\"id-" + symbol + "\",\"symbol\":\"" + symbol + "\"," +
            "\"underlying_symbol\":\"SPY\",\"root_symbol\":\"" + root + "\"," +
            "\"expiration_date\":\"2026-10-16\",\"type\":\"call\",\"style\":\"american\"," +
            "\"strike_price\":\"600\",\"multiplier\":\"100\",\"size\":\"100\"," +
            "\"status\":\"active\",\"tradable\":true}";

        private string Quotes() =>
            NoQuotes
                ? "{\"quotes\":{}}"
                : "{\"quotes\":{\"" + Symbol + "\":{\"ap\":8.2,\"as\":37,\"ax\":\"W\",\"bp\":8.0," +
                  "\"bs\":42,\"bx\":\"W\",\"c\":[\"A\"],\"t\":\"" +
                  (StaleQuotes ? StaleQuoteTimestamp : "2026-09-04T14:59:50Z") + "\"}}}";

        private static string Snapshots() =>
            "{\"snapshots\":{\"" + Symbol + "\":{" +
            "\"greeks\":{\"delta\":0.53,\"gamma\":0.012,\"rho\":0.1,\"theta\":-0.03,\"vega\":0.118}," +
            "\"impliedVolatility\":0.204," +
            "\"latestQuote\":{\"t\":\"2026-09-04T14:59:50Z\"}}}}";

        private static string Bars() =>
            "{\"bars\":{\"" + Symbol + "\":[" +
            "{\"t\":\"2026-09-03T04:00:00Z\",\"o\":8.0,\"h\":8.4,\"l\":7.9,\"c\":8.2,\"v\":410," +
            "\"n\":88,\"vw\":8.15}]},\"next_page_token\":null}";
    }
}
