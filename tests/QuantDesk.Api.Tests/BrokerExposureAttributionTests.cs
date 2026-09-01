using QuantDesk.Api.PaperTrading;
using QuantDesk.Domain.Execution;

namespace QuantDesk.Api.Tests;

/// <summary>
/// Attribution is what lets the entry gate be narrower than "halt on anything".
///
/// The rule it has to get right is asymmetric: being wrong about foreign exposure must halt trading,
/// and being wrong about our own exposure only costs a skipped cycle. Every test here is written from
/// that direction — the failures that matter are the ones where something foreign is treated as ours.
/// </summary>
public sealed class BrokerExposureAttributionTests
{
    [Fact]
    public void ExposureFromOurOwnLaneIsAttributed()
    {
        BrokerExposureAttribution attribution = Attribute(
            claims: [new ExposureClaim("BTC/USD", ["qd-diag-1-entry"])],
            orders: [Order("qd-diag-1-entry")],
            positions: [Position("BTCUSD", 0.5m)]);

        Assert.False(attribution.HasUnattributedExposure);
        Assert.True(attribution.IsClaimed("BTC/USD"));
    }

    [Fact]
    public void AnOrderNoLaneCreatedHaltsEntry()
    {
        BrokerExposureAttribution attribution = Attribute(
            claims: [new ExposureClaim("BTC/USD", ["qd-diag-1-entry"])],
            orders: [Order("someone-elses-order")],
            positions: []);

        Assert.True(attribution.HasUnattributedExposure);
        Assert.Contains("someone-elses-order", attribution.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void APositionInAnUnclaimedSymbolIsForeign()
    {
        // The case the halt exists for. A lane trading BTC must not proceed past an ETH position it
        // cannot explain.
        BrokerExposureAttribution attribution = Attribute(
            claims: [new ExposureClaim("BTC/USD", ["qd-diag-1-entry"])],
            orders: [],
            positions: [Position("ETHUSD", 2m)]);

        Assert.True(attribution.HasUnattributedExposure);
        Assert.False(attribution.IsClaimed("ETH/USD"));
    }

    [Fact]
    public void SymbolsMatchAcrossTheVenuesSlashConvention()
    {
        // The trading API accepts BTC/USD; the positions endpoint reports BTCUSD for the same thing.
        // Getting this wrong would report every crypto position we hold as foreign and halt forever.
        BrokerExposureAttribution attribution = Attribute(
            claims: [new ExposureClaim("BTC/USD", [])],
            orders: [],
            positions: [Position("BTCUSD", 0.5m)]);

        Assert.False(attribution.HasUnattributedExposure);
    }

    [Fact]
    public void AZeroQuantityPositionIsNotExposure()
    {
        BrokerExposureAttribution attribution = Attribute(
            claims: [], orders: [], positions: [Position("ETHUSD", 0m)]);

        Assert.False(attribution.HasUnattributedExposure);
    }

    [Fact]
    public void OneLanesHoldingDoesNotHaltAnother()
    {
        // The whole point of narrowing the gate: the multi-leg lane holding SPY options is no reason to
        // stop the spot lane trading BTC.
        BrokerExposureAttribution attribution = Attribute(
            claims: [new ExposureClaim("SPY261016C00600000", ["qd-mleg-1-entry"])],
            orders: [],
            positions: [Position("SPY261016C00600000", 1m)]);

        Assert.False(attribution.HasUnattributedExposure);
        Assert.False(attribution.IsClaimed("BTC/USD"));
        Assert.True(attribution.IsClaimed("SPY261016C00600000"));
    }

    [Fact]
    public void ALaneThatClaimsNothingLeavesEveryPositionForeign()
    {
        // Fail-closed: a store that cannot be read returns no claims, and the exposure it would have
        // explained is then reported as foreign rather than silently permitted.
        BrokerExposureAttribution attribution = Attribute(
            claims: [], orders: [Order("qd-diag-1-entry")], positions: [Position("BTCUSD", 0.5m)]);

        Assert.True(attribution.HasUnattributedExposure);
        Assert.Single(attribution.UnattributedOrders);
        Assert.Single(attribution.UnattributedPositions);
    }

    [Fact]
    public void ClaimsFromEveryLaneAreCombined()
    {
        var attributor = new BrokerExposureAttributor(
        [
            new StubClaimSource("spot", [new ExposureClaim("BTC/USD", ["spot-entry"])]),
            new StubClaimSource("multi-leg", [new ExposureClaim("SPY261016C00600000", ["mleg-entry"])])
        ]);

        BrokerExposureAttribution attribution = attributor.Attribute(
            [Order("spot-entry"), Order("mleg-entry")],
            [Position("BTCUSD", 0.5m), Position("SPY261016C00600000", 1m)]);

        Assert.False(attribution.HasUnattributedExposure);
        Assert.Equal(2, attribution.ClaimedSymbols.Count);
    }

    private static BrokerExposureAttribution Attribute(
        IReadOnlyList<ExposureClaim> claims,
        IReadOnlyList<BrokerOrderSnapshot> orders,
        IReadOnlyList<BrokerPositionSnapshot> positions) =>
        new BrokerExposureAttributor([new StubClaimSource("test", claims)]).Attribute(orders, positions);

    private static BrokerOrderSnapshot Order(string clientOrderId) =>
        new($"broker-{clientOrderId}", clientOrderId, "new", 0m, null);

    private static BrokerPositionSnapshot Position(string symbol, decimal quantity) =>
        new(symbol, 0, quantity, 100m);

    private sealed class StubClaimSource(string laneName, IReadOnlyList<ExposureClaim> claims)
        : IExposureClaimSource
    {
        public string LaneName => laneName;
        public IReadOnlyList<ExposureClaim> ListClaims() => claims;
    }
}
