using QuantDesk.Domain.Trading;
using QuantDesk.Api.PaperTrading;

namespace QuantDesk.Api.Tests;

/// <summary>
/// The routing that decides which venue endpoint a symbol may be sent to.
///
/// A live defect these pin: two background services took the autonomous lane's execution symbol and
/// sent it to crypto endpoints without checking its asset class. Pointing the lane at SPY produced
/// "invalid symbol: SPY does not match ^[A-Z]+x?/[A-Z]+$" roughly twelve times a minute, and
/// nothing broke -- the failures logged as warnings and the lane ran on, so the only symptom was a
/// research volume quietly missing the data it was supposed to accumulate.
/// </summary>
public sealed class AssetClassRoutingTests
{
    [Theory]
    [InlineData("BTC/USD")]
    [InlineData("ETH/USD")]
    public void ACryptoPairRoutesToSpotCrypto(string symbol)
    {
        Assert.True(new OpportunityRouter().TryRoute(symbol, out OpportunityRoute? route, out _));
        Assert.Equal(TradedAssetClass.SpotCrypto, route!.AssetClass);
    }

    [Theory]
    [InlineData("SPY")]
    [InlineData("QQQ")]
    public void AnEquityDoesNotRouteToSpotCrypto(string symbol)
    {
        // The check the two services were missing. An equity must never reach a crypto endpoint,
        // which can only reject it.
        Assert.True(new OpportunityRouter().TryRoute(symbol, out OpportunityRoute? route, out _));
        Assert.NotEqual(TradedAssetClass.SpotCrypto, route!.AssetClass);
    }

    [Fact]
    public void AnUnroutableSymbolIsRefusedWithAReasonRatherThanGuessed()
    {
        Assert.False(new OpportunityRouter().TryRoute("not a symbol", out _, out string reason));
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }
}

