using System.Globalization;
using QuantDesk.Domain.Runtime;

namespace QuantDesk.Api.PaperTrading;

public sealed record AutonomousPaperTradingOptions(
    bool Enabled,
    AutonomousTradingMode Mode,
    ExperimentalPaperAuthorization? ExperimentalAuthorization,
    string Symbol,
    decimal OrderNotional,
    TimeSpan HoldDuration,
    TimeSpan FillTimeout,
    TimeSpan CycleInterval)
{
    public static AutonomousPaperTradingOptions FromEnvironment(PaperTradingOptions trading)
    {
        AutonomousTradingMode mode = ParseMode();
        bool enabled = mode != AutonomousTradingMode.Disabled && bool.TryParse(
            Environment.GetEnvironmentVariable("QUANTDESK_AUTONOMOUS_ENABLED"),
            out bool configured) && configured;
        string configuredSymbol = (Environment.GetEnvironmentVariable("QUANTDESK_AUTONOMOUS_SYMBOL") ?? "")
            .Trim().ToUpperInvariant();
        // BTC/USD remains the disabled research-data fallback only. An enabled execution lane
        // must name its intended symbol explicitly; it must never inherit a historically failed
        // default venue merely because an environment variable was omitted.
        string symbol = configuredSymbol;
        decimal notional = ParsePositiveDecimal("QUANTDESK_AUTONOMOUS_ORDER_NOTIONAL", 20m);
        int maximumHoldingHours = ParsePositiveInteger("QUANTDESK_AUTONOMOUS_MAX_HOLDING_HOURS", 168);
        int fillTimeoutSeconds = ParsePositiveInteger("QUANTDESK_AUTONOMOUS_FILL_TIMEOUT_SECONDS", 30);
        int cycleIntervalSeconds = ParsePositiveInteger("QUANTDESK_AUTONOMOUS_CYCLE_INTERVAL_SECONDS", 300);

        if (enabled && string.IsNullOrWhiteSpace(symbol))
            throw new InvalidOperationException("QUANTDESK_AUTONOMOUS_SYMBOL is required when autonomous execution is enabled.");
        if (enabled && !trading.Symbols.Values.Contains(symbol, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("QUANTDESK_AUTONOMOUS_SYMBOL must be present in QUANTDESK_SYMBOLS.");
        if (!enabled && string.IsNullOrWhiteSpace(symbol)) symbol = "BTC/USD";
        if (notional > trading.MaximumOrderNotional)
            throw new InvalidOperationException("Autonomous order notional exceeds the configured paper-order limit.");

        ExperimentalPaperAuthorization? authorization = mode == AutonomousTradingMode.ExperimentalPaper
            ? ReadExperimentalAuthorization(symbol)
            : null;

        return new(
            enabled,
            mode,
            authorization,
            symbol,
            notional,
            TimeSpan.FromHours(maximumHoldingHours),
            TimeSpan.FromSeconds(fillTimeoutSeconds),
            TimeSpan.FromSeconds(cycleIntervalSeconds));
    }

    private static ExperimentalPaperAuthorization ReadExperimentalAuthorization(string symbol)
    {
        string Get(string name) => Environment.GetEnvironmentVariable(name)?.Trim() ?? "";
        bool Passed(string name) => bool.TryParse(Environment.GetEnvironmentVariable(name), out bool value) && value;
        var authorization = new ExperimentalPaperAuthorization(
            Get("QUANTDESK_EXPERIMENT_ID"), Get("QUANTDESK_HYPOTHESIS_ID"),
            Get("QUANTDESK_STRATEGY_VERSION"), symbol, ParseRegisteredAt(), Get("QUANTDESK_EVIDENCE_REFERENCE"),
            Passed("QUANTDESK_LEAKAGE_SANITY_PASSED"), Passed("QUANTDESK_REPLAY_SANITY_PASSED"));
        if (!authorization.IsValidFor(symbol))
            throw new InvalidOperationException("Experimental paper authorization is incomplete or failed sanity checks.");
        return authorization;
    }

    private static DateTimeOffset ParseRegisteredAt()
    {
        string text = Environment.GetEnvironmentVariable("QUANTDESK_EXPERIMENT_REGISTERED_AT") ?? "";
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset value)
            ? value
            : throw new InvalidOperationException("QUANTDESK_EXPERIMENT_REGISTERED_AT must be an ISO-8601 timestamp.");
    }

    private static AutonomousTradingMode ParseMode()
    {
        string value = Environment.GetEnvironmentVariable("QUANTDESK_AUTONOMOUS_MODE") ?? "Disabled";
        return Enum.TryParse(value, ignoreCase: true, out AutonomousTradingMode mode)
            ? mode
            : throw new InvalidOperationException("QUANTDESK_AUTONOMOUS_MODE must be Disabled, ExperimentalPaper, or ValidatedPaper.");
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
