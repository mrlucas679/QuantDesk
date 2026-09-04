namespace QuantDesk.Domain.Trading;

/// <summary>
/// What kind of instrument an opportunity is in.
///
/// This lives in the domain because three separate layers need the same answer and were each
/// deciding for themselves. The router knew a symbol was an equity; the strategy compiler did not,
/// and checked crypto permission and booked crypto beta for it regardless. An asset class that only
/// the routing layer understands is not an asset class, it is a routing detail — and the parts that
/// price risk and ask the venue for permission are exactly the parts that must not guess.
/// </summary>
public enum TradedAssetClass
{
    SpotCrypto,
    UsEquity,
    UsEquityOption
}
