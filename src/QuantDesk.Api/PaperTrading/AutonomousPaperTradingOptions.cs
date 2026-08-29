using System.Globalization;

namespace QuantDesk.Api.PaperTrading;

public sealed record AutonomousPaperTradingOptions(
    bool Enabled,
    string Symbol,
    decimal OrderNotional,
    TimeSpan HoldDuration,
    TimeSpan FillTimeout)
{
    public static AutonomousPaperTradingOptions FromEnvironment(PaperTradingOptions trading)
    {
        bool enabled = bool.TryParse(
            Environment.GetEnvironmentVariable("QUANTDESK_AUTONOMOUS_ENABLED"),
            out bool configured) && configured;
        string symbol = (Environment.GetEnvironmentVariable("QUANTDESK_AUTONOMOUS_SYMBOL") ?? "BTC/USD")
            .Trim().ToUpperInvariant();
        decimal notional = ParsePositiveDecimal("QUANTDESK_AUTONOMOUS_ORDER_NOTIONAL", 20m);
        int holdSeconds = ParsePositiveInteger("QUANTDESK_AUTONOMOUS_HOLD_SECONDS", 5);
        int fillTimeoutSeconds = ParsePositiveInteger("QUANTDESK_AUTONOMOUS_FILL_TIMEOUT_SECONDS", 30);

        if (enabled && !trading.Symbols.Values.Contains(symbol, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("QUANTDESK_AUTONOMOUS_SYMBOL must be present in QUANTDESK_SYMBOLS.");
        if (notional > trading.MaximumOrderNotional)
            throw new InvalidOperationException("Autonomous order notional exceeds the configured paper-order limit.");

        return new(enabled, symbol, notional, TimeSpan.FromSeconds(holdSeconds), TimeSpan.FromSeconds(fillTimeoutSeconds));
    }

    private static decimal ParsePositiveDecimal(string name, decimal fallback)
    {
        string text = Environment.GetEnvironmentVariable(name) ?? fallback.ToString(CultureInfo.InvariantCulture);
        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal value) && value > 0
            ? value
            : throw new InvalidOperationException($"{name} must be a positive decimal value.");
    }

    private static int ParsePositiveInteger(string name, int fallback)
    {
        string text = Environment.GetEnvironmentVariable(name) ?? fallback.ToString(CultureInfo.InvariantCulture);
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value > 0
            ? value
            : throw new InvalidOperationException($"{name} must be a positive integer value.");
    }
}
