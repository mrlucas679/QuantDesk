using System.Globalization;

namespace QuantDesk.Api.PaperTrading;

public sealed record PaperTradingOptions(
    decimal MaximumOrderNotional,
    IReadOnlyDictionary<int, string> Symbols)
{
    public static PaperTradingOptions FromEnvironment()
    {
        string[] symbols = (Environment.GetEnvironmentVariable("QUANTDESK_SYMBOLS") ?? "SPY")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(symbol => symbol.ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (symbols.Length == 0 || symbols.Any(symbol => !IsValidSymbol(symbol)))
            throw new InvalidOperationException("QUANTDESK_SYMBOLS must contain valid comma-separated broker symbols.");

        string maximumText = Environment.GetEnvironmentVariable("QUANTDESK_MAX_PAPER_ORDER_NOTIONAL") ?? "1000";
        if (!decimal.TryParse(maximumText, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal maximum) || maximum <= 0)
            throw new InvalidOperationException("QUANTDESK_MAX_PAPER_ORDER_NOTIONAL must be a positive decimal value.");

        return new PaperTradingOptions(maximum, symbols.Index().ToDictionary(item => item.Index, item => item.Item));
    }

    private static bool IsValidSymbol(string symbol) => symbol.Length is > 0 and <= 24 &&
        symbol.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '/' or '-');
}
