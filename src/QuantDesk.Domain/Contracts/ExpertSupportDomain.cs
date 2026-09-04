namespace QuantDesk.Domain.Contracts;

/// <summary>
/// What a fitted model was actually fitted on, and therefore what it may be asked about.
///
/// The blueprint has named this since §44 and nothing implemented it. The consequence was live and
/// silent: there was exactly one <c>har</c> artifact and one <c>garch</c> artifact, both fitted on
/// the BTC/USD five-minute series, and both were consulted for SPY, QQQ, IWM and DIA. Nothing was
/// broken in a way anything could detect -- the schema hashes matched, the parity cases reproduced,
/// the coefficients loaded -- because the artifact never said what it was fitted on, so no check
/// could compare it against what it was being asked.
///
/// A variance model carried across instruments is not a slightly worse variance model. Bitcoin's
/// realised variance and an equity index ETF's differ by roughly an order of magnitude, and their
/// session structure differs completely: crypto trades continuously, an ETF has an opening auction,
/// a lunchtime lull and a close. A HAR fitted on one and applied to the other produces confident
/// numbers on every bar, and every gate downstream treats them as a forecast.
///
/// So the artifact declares its domain, and the runtime refuses outside it. §54's blind-spot
/// register calls this BS-006, "model applied outside validated feed/instrument/session".
/// </summary>
/// <param name="AssetClass">
/// The venue class it was fitted against, as the research plane names it (<c>spot_crypto</c>,
/// <c>us_equity</c>). Compared case-insensitively; an empty value supports nothing.
/// </param>
/// <param name="Symbols">
/// The instruments in the fitting dataset. A model fitted on a basket may legitimately name several;
/// one fitted on BTC/USD names exactly that. Empty supports nothing.
/// </param>
/// <param name="BarDurationMinutes">
/// The bar the features were computed on. Carried separately from the feature schema hash because
/// the hash covers names and ordering, not the sampling interval: a HAR fitted on five-minute bars
/// and fed one-minute bars has an identical schema hash and a different model.
/// </param>
public sealed record ExpertSupportDomain(
    string AssetClass,
    IReadOnlyList<string> Symbols,
    int BarDurationMinutes)
{
    /// <summary>
    /// The domain of an artifact that declared none, which supports nothing.
    ///
    /// Deliberately not "supports everything". Every artifact written before this field existed
    /// omits it, and those are precisely the artifacts whose reach was never established -- reading
    /// silence as universal permission would preserve the defect this type exists to end, and do it
    /// while looking like a compatibility shim.
    /// </summary>
    public static readonly ExpertSupportDomain Undeclared = new(string.Empty, [], 0);

    /// <summary>True when this domain names an instrument at all.</summary>
    public bool IsDeclared =>
        !string.IsNullOrWhiteSpace(AssetClass) && Symbols.Count > 0 && BarDurationMinutes > 0;

    /// <summary>Whether this model may be asked about <paramref name="symbol"/> at this bar.</summary>
    public bool Supports(string symbol, int barDurationMinutes)
    {
        if (!IsDeclared || string.IsNullOrWhiteSpace(symbol)) return false;
        if (barDurationMinutes != BarDurationMinutes) return false;

        return Symbols.Any(declared =>
            string.Equals(declared, symbol, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Why a lookup was refused, in terms an operator can act on.</summary>
    public string ExplainRefusal(string symbol, int barDurationMinutes) => !IsDeclared
        ? "artifact declares no support domain"
        : barDurationMinutes != BarDurationMinutes
            ? $"fitted on {BarDurationMinutes}-minute bars, asked for {barDurationMinutes}"
            : $"fitted on {string.Join(", ", Symbols)}, asked about {symbol}";
}
