using QuantDesk.Domain.Trading;

namespace QuantDesk.Domain.Market;

/// <summary>
/// The bar each lane computes on, in one place so the two ends cannot drift apart.
///
/// The quote client requests a timeframe, the rule registry holds figures measured on a timeframe,
/// and the position sizer counts a holding period in bars of a timeframe. Those three must agree,
/// and they had no shared statement of what the answer was -- five minutes was hardcoded separately
/// in each, never chosen, only inherited.
///
/// Why the two lanes differ
/// ------------------------
/// Every equity figure measured on five-minute bars is negative, so the equity book has been empty
/// since it was written and the lane has never opened an equity position. Rescanned on thirty-minute
/// bars, three families clear their cost at the 95% lower bound. Crypto has no survivor at any
/// timeframe scanned and keeps its five-minute clock, where its evidence was gathered.
/// </summary>
public static class LaneBars
{
    /// <summary>Crypto trades continuously and its evidence was gathered on five-minute bars.</summary>
    public const int SpotCryptoMinutes = 5;

    /// <summary>
    /// Equities read thirty-minute bars, which is where their only surviving evidence lives.
    ///
    /// It also decides whether this system can ever hold a short. Crypto was the only book that
    /// traded, crypto has no borrow at this venue, and so all 262 filled orders to date were long.
    /// Equities can be shorted; giving them a clock on which they actually signal is what makes the
    /// short path reachable rather than merely implemented.
    /// </summary>
    public const int UsEquityMinutes = 30;

    /// <summary>The bar this asset class's lane computes on.</summary>
    public static int For(TradedAssetClass assetClass) => assetClass switch
    {
        TradedAssetClass.SpotCrypto => SpotCryptoMinutes,
        _ => UsEquityMinutes,
    };

    /// <inheritdoc cref="For(TradedAssetClass)"/>
    public static TimeSpan DurationFor(TradedAssetClass assetClass) =>
        TimeSpan.FromMinutes(For(assetClass));
}
