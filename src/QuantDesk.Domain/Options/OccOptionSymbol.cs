using System.Globalization;
using System.Text.RegularExpressions;

namespace QuantDesk.Domain.Options;

public sealed record OccOptionSymbol(
    string BrokerSymbol,
    string Underlying,
    DateOnly Expiration,
    OptionRight Right,
    decimal Strike)
{
    /// <summary>
    /// OCC symbology: a root of one to six characters, then a six-digit expiration, a call/put flag,
    /// and an eight-digit strike in thousandths.
    ///
    /// The root may contain digits after its first character, and that is not a curiosity — it is how
    /// a corporate action is encoded. An adjusted contract carries a numbered root (<c>SPY1</c>,
    /// <c>AAPL1</c>) while its underlying stays <c>SPY</c>. Restricting the root to letters rejected
    /// every adjusted contract as an *invalid symbol*, which is the wrong reading twice over: the
    /// symbol is perfectly valid, and callers that treat an unparseable symbol as a corrupt feed would
    /// discard a whole chain over one ordinary contract. Parsing it lets the caller see what it is and
    /// decide — this system excludes adjusted contracts, because their deliverable is non-standard.
    ///
    /// The trailing fifteen characters are fixed-width, so a digit-bearing root stays unambiguous: the
    /// greedy root backtracks until the date, flag, and strike all match.
    /// </summary>
    private static readonly Regex Pattern = new(
        "^(?<root>[A-Z][A-Z0-9]{0,5})(?<date>[0-9]{6})(?<right>[CP])(?<strike>[0-9]{8})$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool TryParse(string value, out OccOptionSymbol? symbol)
    {
        symbol = null;
        if (string.IsNullOrWhiteSpace(value)) return false;
        string normalized = value.Trim().ToUpperInvariant();
        Match match = Pattern.Match(normalized);
        if (!match.Success ||
            !DateOnly.TryParseExact(match.Groups["date"].Value, "yyMMdd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly expiration) ||
            !int.TryParse(match.Groups["strike"].Value, NumberStyles.None,
                CultureInfo.InvariantCulture, out int strikeThousandths) ||
            strikeThousandths <= 0)
            return false;
        symbol = new OccOptionSymbol(
            normalized,
            match.Groups["root"].Value,
            expiration,
            match.Groups["right"].Value == "C" ? OptionRight.Call : OptionRight.Put,
            strikeThousandths / 1000m);
        return true;
    }
}
