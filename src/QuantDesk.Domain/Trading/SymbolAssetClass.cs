namespace QuantDesk.Domain.Trading;

/// <summary>
/// Which book a symbol belongs to, from the symbol alone.
///
/// Nothing in the venue's data records the asset class beside a symbol, so it has to be derived --
/// and a rule that has to be inferred is worth stating in one place rather than at each site that
/// needs it. It was being inferred in three already, once in Python and twice in C#, and three
/// copies of a rule are three chances for one of them to drift.
///
/// The rule is Alpaca's: crypto pairs are slash-separated (<c>BTC/USD</c>, <c>ETH/USD</c>) and
/// equities are not (<c>SPY</c>, <c>QQQ</c>). It is a venue convention rather than a law, which is
/// the other reason to keep it in one place: when it stops being true there is one thing to change.
/// </summary>
public static class SymbolAssetClass
{
    /// <summary>The book <paramref name="symbol"/> trades on.</summary>
    /// <remarks>
    /// An option symbol is not distinguished here. Options reach the runtime through their own
    /// lane, with their asset class carried on the route rather than guessed from the contract
    /// symbol, so widening this would add a case nothing asks for and invite it to be trusted.
    /// </remarks>
    public static TradedAssetClass Of(string? symbol) =>
        symbol is not null && symbol.Contains('/', StringComparison.Ordinal)
            ? TradedAssetClass.SpotCrypto
            : TradedAssetClass.UsEquity;
}
