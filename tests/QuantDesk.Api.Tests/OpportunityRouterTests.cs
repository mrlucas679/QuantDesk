using QuantDesk.Api.PaperTrading;
using QuantDesk.Domain.Capabilities;
using QuantDesk.Domain.Trading;

namespace QuantDesk.Api.Tests;

public sealed class OpportunityRouterTests
{
    private static readonly OpportunityRouter Router = new();

    [Theory]
    [InlineData("BTC/USD", TradedAssetClass.SpotCrypto)]
    [InlineData("eth/usd", TradedAssetClass.SpotCrypto)]
    [InlineData("SPY", TradedAssetClass.UsEquity)]
    [InlineData("qqq", TradedAssetClass.UsEquity)]
    [InlineData("SPY260904C00600000", TradedAssetClass.UsEquityOption)]
    public void RecognisedSymbolsRouteToTheirAssetClass(string symbol, TradedAssetClass expected)
    {
        Assert.True(Router.TryRoute(symbol, out OpportunityRoute? route, out _));
        Assert.Equal(expected, route!.AssetClass);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("TOOLONGSYMBOL")]
    [InlineData("SP-Y")]
    [InlineData("BTC/USD/EUR")]
    [InlineData("BTC/")]
    [InlineData("123")]
    public void UnrecognisedSymbolsFailClosedRatherThanTakingADefaultRoute(string? symbol)
    {
        Assert.False(Router.TryRoute(symbol, out OpportunityRoute? route, out string reason));
        Assert.Null(route);
        Assert.NotEqual("Routed", reason);
    }

    [Fact]
    public void EachAssetClassCarriesItsOwnVenueCost()
    {
        Router.TryRoute("BTC/USD", out OpportunityRoute? crypto, out _);
        Router.TryRoute("SPY", out OpportunityRoute? equity, out _);
        Router.TryRoute("SPY260904C00600000", out OpportunityRoute? option, out _);

        // Spot crypto must clear 71 bps against the equity lane's 9 bps on the same one-bp spread —
        // a 7.9x gap, and the whole reason a crypto-only lane never produced an admissible
        // opportunity. Options sit between the two: no commission, but far wider spreads.
        Assert.Equal(71m, crypto!.Costs.HurdleBps(1m));
        Assert.Equal(9m, equity!.Costs.HurdleBps(1m));
        Assert.Equal(32m, option!.Costs.HurdleBps(1m));
        Assert.True(crypto.Costs.HurdleBps(1m) > 7 * equity.Costs.HurdleBps(1m));
    }

    [Fact]
    public void NoRouteIsPermittedOutsideAPaperEnvironment()
    {
        var live = new AccountCapabilities(false, true, true, true, 3);

        foreach (string symbol in new[] { "BTC/USD", "SPY", "SPY260904C00600000" })
        {
            Router.TryRoute(symbol, out OpportunityRoute? route, out _);
            Assert.False(route!.IsPermittedBy(live));
        }
    }

    [Fact]
    public void EachRouteRequiresItsOwnAccountPermission()
    {
        Router.TryRoute("BTC/USD", out OpportunityRoute? crypto, out _);
        Router.TryRoute("SPY", out OpportunityRoute? equity, out _);
        Router.TryRoute("SPY260904C00600000", out OpportunityRoute? option, out _);

        var cryptoOnly = new AccountCapabilities(true, false, true, false, null);
        Assert.True(crypto!.IsPermittedBy(cryptoOnly));
        Assert.False(equity!.IsPermittedBy(cryptoOnly));
        Assert.False(option!.IsPermittedBy(cryptoOnly));

        var fullyEnabled = new AccountCapabilities(true, true, true, true, 3);
        Assert.True(equity.IsPermittedBy(fullyEnabled));
        Assert.True(option.IsPermittedBy(fullyEnabled));
    }

    [Fact]
    public void SpreadsRequireOptionsLevelTwoOrAbove()
    {
        Router.TryRoute("SPY260904C00600000", out OpportunityRoute? option, out _);

        Assert.False(option!.IsPermittedBy(new AccountCapabilities(true, true, true, true, 1)));
        Assert.True(option.IsPermittedBy(new AccountCapabilities(true, true, true, true, 2)));
    }

    [Fact]
    public void EveryRouteBoundsItsFillPriceInsteadOfAcceptingAnyMarketPrice()
    {
        foreach (string symbol in new[] { "BTC/USD", "SPY", "SPY260904C00600000" })
        {
            Router.TryRoute(symbol, out OpportunityRoute? route, out _);
            Assert.Equal(ExecutionOrderType.Limit, route!.OrderPolicy.OrderType);
            Assert.NotNull(route.OrderPolicy.BuyLimitPrice(100m));
        }
    }

    [Fact]
    public void MarketableLimitCrossesTheSpreadButCapsTheWorstFill()
    {
        OrderExecutionPolicy policy = OrderExecutionPolicy.MarketableLimit;

        decimal buyLimit = policy.BuyLimitPrice(100m)!.Value;
        decimal sellLimit = policy.SellLimitPrice(100m)!.Value;

        Assert.True(buyLimit > 100m, "A marketable buy must cross the offer to fill.");
        Assert.True(buyLimit <= 100.2m, "A marketable buy must still cap the worst acceptable fill.");
        Assert.True(sellLimit < 100m && sellLimit >= 99.8m);
    }

    [Fact]
    public void AnUnboundedMarketOrderCarriesNoLimitPrice()
    {
        Assert.Null(OrderExecutionPolicy.UnboundedMarket.BuyLimitPrice(100m));
        Assert.Null(OrderExecutionPolicy.UnboundedMarket.SellLimitPrice(100m));
    }

    [Fact]
    public void MakerCryptoCostsLessThanTakerCrypto() =>
        Assert.True(
            ExecutionCostProfile.SpotCryptoMaker.HurdleBps(1m) <
            ExecutionCostProfile.SpotCryptoTaker.HurdleBps(1m));

    [Fact]
    public void CryptoCostScenariosRemainExplicitAndDoNotConflateObservedWithQualification()
    {
        ExecutionCostProfile observed = ExecutionCostProfile.ObservedRealisedCrypto(50m, 7m, "run-42");

        Assert.Equal("spot-crypto-observed-realised:run-42", observed.AssetClass);
        Assert.True(ExecutionCostProfile.SpotCryptoConservativeStress.HurdleBps(1m) >
            ExecutionCostProfile.SpotCryptoTaker.HurdleBps(1m));
        Assert.True(observed.HurdleBps(1m) <
            ExecutionCostProfile.SpotCryptoConservativeStress.HurdleBps(1m));
    }
}
