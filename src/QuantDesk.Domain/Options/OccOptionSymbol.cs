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
    private static readonly Regex Pattern = new(
        "^(?<root>[A-Z]{1,6})(?<date>[0-9]{6})(?<right>[CP])(?<strike>[0-9]{8})$",
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
