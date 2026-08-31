using System.Globalization;

namespace QuantDesk.Api.PaperTrading;

/// <summary>Defines the deliberately small cash envelope for broker-path diagnostics.</summary>
public sealed record DiagnosticExecutionOptions(decimal MaximumNotional)
{
    public const string RequiredSymbol = "BTC/USD";
    public static readonly TimeSpan HoldingDuration = TimeSpan.FromMinutes(2);

    public static DiagnosticExecutionOptions FromEnvironment(PaperTradingOptions paperTrading)
    {
        ArgumentNullException.ThrowIfNull(paperTrading);
        string maximumText = Environment.GetEnvironmentVariable("QUANTDESK_DIAGNOSTIC_MAX_NOTIONAL") ?? "5";
        if (!decimal.TryParse(maximumText, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal maximum) ||
            maximum <= 0 || maximum > paperTrading.MaximumOrderNotional)
        {
            throw new InvalidOperationException(
                "QUANTDESK_DIAGNOSTIC_MAX_NOTIONAL must be positive and no greater than the paper-order limit.");
        }

        return new DiagnosticExecutionOptions(maximum);
    }

    public bool Allows(string symbol, decimal notional) =>
        string.Equals(symbol.Trim(), RequiredSymbol, StringComparison.OrdinalIgnoreCase) &&
        notional > 0 && notional <= MaximumNotional;
}
