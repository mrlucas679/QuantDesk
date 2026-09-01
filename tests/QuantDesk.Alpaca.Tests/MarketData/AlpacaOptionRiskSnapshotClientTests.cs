using System.Net;
using System.Text;
using QuantDesk.Alpaca.Configuration;
using QuantDesk.Alpaca.MarketData;
using QuantDesk.Domain.Options;
using QuantDesk.Domain.Runtime;

namespace QuantDesk.Alpaca.Tests.MarketData;

/// <summary>
/// These payloads are Alpaca's documented option-snapshot shape, not a convenient stand-in for it.
///
/// The distinction is the whole point of this file. The previous fixture put implied volatility inside
/// <c>greeks</c> as <c>implied_volatility</c>; the venue puts it beside <c>greeks</c> as
/// <c>impliedVolatility</c>. A test written against the invented shape passed against the code written
/// for the invented shape, and the pair proved nothing about the venue — the lane would have failed on
/// its first real response.
/// </summary>
public sealed class AlpacaOptionRiskSnapshotClientTests
{
    private const string Symbol = "SPY260918C00600000";
    private static readonly DateTimeOffset AsOf = DateTimeOffset.Parse("2026-08-31T15:00:30Z");

    [Fact]
    public async Task ReadsGreeksAndImpliedVolatilityFromAVenueShapedSnapshot()
    {
        IReadOnlyDictionary<int, OptionRiskSnapshot> snapshots = await SnapshotAsync("""
            {"snapshots":{"SPY260918C00600000":{
              "dailyBar":{"c":3.1,"h":3.4,"l":2.9,"n":812,"o":3.0,"t":"2026-08-31T04:00:00Z","v":4210,"vw":3.12},
              "greeks":{"delta":0.5312,"gamma":0.0121,"rho":0.1043,"theta":-0.0298,"vega":0.1187},
              "impliedVolatility":0.2043,
              "latestQuote":{"ap":3.15,"as":42,"ax":"W","bp":3.05,"bs":37,"bx":"W","c":"A",
                             "t":"2026-08-31T15:00:00Z"},
              "latestTrade":{"c":"a","p":3.1,"s":2,"t":"2026-08-31T14:59:58Z","x":"W"}}}}
            """);

        OptionRiskSnapshot snapshot = snapshots[42];
        Assert.Equal(DataQuality.Healthy, snapshot.Quality);
        Assert.Equal(0.2043, snapshot.ImpliedVolatility);
        Assert.Equal(0.5312, snapshot.Delta);
        Assert.Equal(0.0121, snapshot.Gamma);
        Assert.Equal(0.1187, snapshot.Vega);
        Assert.Equal(-0.0298, snapshot.Theta);
    }

    [Fact]
    public async Task ASnapshotWithoutImpliedVolatilityDegradesInsteadOfThrowing()
    {
        // An absent property deserializes to an Undefined JsonElement, and reading a string from that
        // throws. Degrading is what lets one unpriced contract leave the rest of a chain usable.
        IReadOnlyDictionary<int, OptionRiskSnapshot> snapshots = await SnapshotAsync("""
            {"snapshots":{"SPY260918C00600000":{
              "greeks":{"delta":0.5,"gamma":0.01,"rho":0.1,"theta":-0.03,"vega":0.12},
              "latestQuote":{"t":"2026-08-31T15:00:00Z"}}}}
            """);

        Assert.Equal(DataQuality.Stale, snapshots[42].Quality);
    }

    [Fact]
    public async Task GreeksSentAsStringsAreStillRead()
    {
        IReadOnlyDictionary<int, OptionRiskSnapshot> snapshots = await SnapshotAsync("""
            {"snapshots":{"SPY260918C00600000":{
              "greeks":{"delta":"0.5","gamma":"0.01","rho":"0.1","theta":"-0.03","vega":"0.12"},
              "impliedVolatility":"0.2",
              "latestQuote":{"t":"2026-08-31T15:00:00Z"}}}}
            """);

        Assert.Equal(DataQuality.Healthy, snapshots[42].Quality);
        Assert.Equal(0.2, snapshots[42].ImpliedVolatility);
    }

    [Fact]
    public async Task AMissingGreeksBlockRemainsExplicitlyStale()
    {
        IReadOnlyDictionary<int, OptionRiskSnapshot> snapshots = await SnapshotAsync("""
            {"snapshots":{"SPY260918C00600000":{"impliedVolatility":0.2,
              "latestQuote":{"t":"2026-08-31T15:00:00Z"}}}}
            """);

        Assert.Equal(DataQuality.Stale, snapshots[42].Quality);
    }

    [Fact]
    public async Task APartialGreeksSetIsRefusedRatherThanZeroFilled()
    {
        // A missing delta reported as zero would read as a genuinely delta-neutral position and could
        // size risk to nothing. Greeks are only meaningful together.
        IReadOnlyDictionary<int, OptionRiskSnapshot> snapshots = await SnapshotAsync("""
            {"snapshots":{"SPY260918C00600000":{
              "greeks":{"gamma":0.01,"theta":-0.03,"vega":0.12},
              "impliedVolatility":0.2,
              "latestQuote":{"t":"2026-08-31T15:00:00Z"}}}}
            """);

        Assert.Equal(DataQuality.Stale, snapshots[42].Quality);
        Assert.Equal(0, snapshots[42].Delta);
    }

    [Fact]
    public async Task AQuoteOlderThanTheCallerAllowsIsStale()
    {
        IReadOnlyDictionary<int, OptionRiskSnapshot> snapshots = await SnapshotAsync("""
            {"snapshots":{"SPY260918C00600000":{
              "greeks":{"delta":0.5,"gamma":0.01,"rho":0.1,"theta":-0.03,"vega":0.12},
              "impliedVolatility":0.2,
              "latestQuote":{"t":"2026-08-31T14:40:00Z"}}}}
            """);

        Assert.Equal(DataQuality.Stale, snapshots[42].Quality);
    }

    [Theory]
    [InlineData("2026-08-31T15:00:31.0000000Z", DataQuality.Healthy)]  // half a second of clock drift
    [InlineData("2026-08-31T15:00:45.0000000Z", DataQuality.Stale)]    // fifteen seconds is not drift
    public async Task ASnapshotStampedAheadOfTheCallersClockIsBelievedOnlyWithinASmallSkew(
        string timestamp, DataQuality expected)
    {
        IReadOnlyDictionary<int, OptionRiskSnapshot> snapshots = await SnapshotAsync(
            "{\"snapshots\":{\"" + Symbol + "\":{" +
            "\"greeks\":{\"delta\":0.5,\"gamma\":0.01,\"rho\":0.1,\"theta\":-0.03,\"vega\":0.12}," +
            "\"impliedVolatility\":0.2," +
            "\"latestQuote\":{\"t\":\"" + timestamp + "\"}}}}");

        Assert.Equal(expected, snapshots[42].Quality);
    }

    [Fact]
    public async Task AContractTheVenueOmittedIsStaleRatherThanAbsent()
    {
        // Every requested slot must come back, or a caller iterating its own slots would throw on a
        // lookup instead of seeing that this leg cannot be priced.
        IReadOnlyDictionary<int, OptionRiskSnapshot> snapshots = await SnapshotAsync("""{"snapshots":{}}""");

        Assert.Equal(DataQuality.Stale, snapshots[42].Quality);
    }

    [Fact]
    public async Task AnUnrequestedSymbolFailsTheRead()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => SnapshotAsync("""
            {"snapshots":{"QQQ260918C00600000":{"latestQuote":{"t":"2026-08-31T15:00:00Z"}}}}
            """));
    }

    [Fact]
    public async Task AVenueRefusalReportsTheStatusAndTheVenuesOwnExplanation()
    {
        HttpRequestException failure = await Assert.ThrowsAsync<HttpRequestException>(() => SnapshotAsync(
            """{"code":40110000,"message":"subscription does not permit querying option data"}""",
            HttpStatusCode.Forbidden));

        Assert.Equal(HttpStatusCode.Forbidden, failure.StatusCode);
        Assert.Contains("v1beta1/options/snapshots", failure.Message, StringComparison.Ordinal);
        Assert.Contains("code 40110000", failure.Message, StringComparison.Ordinal);
        Assert.Contains("subscription does not permit", failure.Message, StringComparison.Ordinal);
    }

    private static async Task<IReadOnlyDictionary<int, OptionRiskSnapshot>> SnapshotAsync(
        string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        using var http = new HttpClient(new StubHandler(body, status));
        var client = new AlpacaOptionRiskSnapshotClient(http, Options());
        return await client.GetSnapshotsAsync(
            new Dictionary<string, int> { [Symbol] = 42 },
            AsOf,
            TimeSpan.FromMinutes(1),
            CancellationToken.None);
    }

    private static AlpacaOptions Options() => new()
    {
        BaseUrl = new Uri("https://paper-api.alpaca.markets"),
        DataBaseUrl = new Uri("https://data.alpaca.markets/"),
        KeyId = "test",
        SecretKey = "test"
    };

    private sealed class StubHandler(string body, HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }
}
