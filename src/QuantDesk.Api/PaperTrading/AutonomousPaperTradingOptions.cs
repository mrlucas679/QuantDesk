using System.Globalization;
using QuantDesk.Domain.Runtime;

namespace QuantDesk.Api.PaperTrading;

/// <summary>How an approved directional view is expressed as a position.</summary>
public enum OpportunityExpression
{
    /// <summary>Hold the underlying instrument directly.</summary>
    Spot,

    /// <summary>
    /// Express the view as a two-leg debit vertical whose maximum loss is the premium paid.
    /// The configured symbol stays the underlying; the chain is discovered at runtime.
    /// </summary>
    DefinedRiskVertical
}

public sealed record AutonomousPaperTradingOptions(
    bool Enabled,
    AutonomousTradingMode Mode,
    OpportunityExpression Expression,
    ExperimentalPaperAuthorization? ExperimentalAuthorization,
    IReadOnlyList<string> Symbols,
    decimal OrderNotional,
    TimeSpan HoldDuration,
    TimeSpan FillTimeout,
    TimeSpan CycleInterval)
{
    /// <summary>
    /// The first configured symbol.
    ///
    /// Kept for the places that genuinely concern the lane as a whole rather than one instrument --
    /// the experimental authorization record, and operator-facing messages before a symbol is in
    /// scope. Anything that prices, routes, or executes must use the symbol it is actually working
    /// on, because those are exactly the decisions that go wrong silently when they inherit a
    /// default.
    /// </summary>
    public string Symbol => Symbols[0];

    public static AutonomousPaperTradingOptions FromEnvironment(PaperTradingOptions trading)
    {
        AutonomousTradingMode mode = ParseMode();
        bool enabled = mode != AutonomousTradingMode.Disabled && bool.TryParse(
            Environment.GetEnvironmentVariable("QUANTDESK_AUTONOMOUS_ENABLED"),
            out bool configured) && configured;
        // Comma-separated, so one lane can work several instruments. A single value stays valid and
        // means exactly what it did before.
        string[] configuredSymbols =
        [
            .. (Environment.GetEnvironmentVariable("QUANTDESK_AUTONOMOUS_SYMBOL") ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(item => item.ToUpperInvariant())
                .Distinct(StringComparer.Ordinal),
        ];
        string configuredSymbol = configuredSymbols.Length > 0 ? configuredSymbols[0] : "";
        // BTC/USD remains the disabled research-data fallback only. An enabled execution lane
        // must name its intended symbol explicitly; it must never inherit a historically failed
        // default venue merely because an environment variable was omitted.
        string symbol = configuredSymbol;
        decimal notional = ParsePositiveDecimal("QUANTDESK_AUTONOMOUS_ORDER_NOTIONAL", 20m);
        int maximumHoldingHours = ParsePositiveInteger("QUANTDESK_AUTONOMOUS_MAX_HOLDING_HOURS", 168);
        int fillTimeoutSeconds = ParsePositiveInteger("QUANTDESK_AUTONOMOUS_FILL_TIMEOUT_SECONDS", 30);
        int cycleIntervalSeconds = ParsePositiveInteger("QUANTDESK_AUTONOMOUS_CYCLE_INTERVAL_SECONDS", 300);

        if (enabled && configuredSymbols.Length == 0)
            throw new InvalidOperationException("QUANTDESK_AUTONOMOUS_SYMBOL is required when autonomous execution is enabled.");
        // Every symbol must be one the market-data plane subscribes to. Naming one it does not
        // stream would leave the lane waiting for evidence that never arrives.
        foreach (string candidate in configuredSymbols)
        {
            if (enabled && !trading.Symbols.Values.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"QUANTDESK_AUTONOMOUS_SYMBOL '{candidate}' must be present in QUANTDESK_SYMBOLS.");
        }

        string[] symbols = configuredSymbols.Length > 0 ? configuredSymbols : ["BTC/USD"];
        if (notional > trading.MaximumOrderNotional)
            throw new InvalidOperationException("Autonomous order notional exceeds the configured paper-order limit.");

        ExperimentalPaperAuthorization? authorization = mode == AutonomousTradingMode.ExperimentalPaper
            ? ReadExperimentalAuthorization(symbols)
            : null;

        return new(
            enabled,
            mode,
            ParseExpression(),
            authorization,
            symbols,
            notional,
            TimeSpan.FromHours(maximumHoldingHours),
            TimeSpan.FromSeconds(fillTimeoutSeconds),
            TimeSpan.FromSeconds(cycleIntervalSeconds));
    }

    private static OpportunityExpression ParseExpression() =>
        Enum.TryParse(
            Environment.GetEnvironmentVariable("QUANTDESK_AUTONOMOUS_EXPRESSION"),
            ignoreCase: true, out OpportunityExpression parsed)
            ? parsed
            : OpportunityExpression.Spot;

    private static ExperimentalPaperAuthorization ReadExperimentalAuthorization(IReadOnlyList<string> symbols)
    {
        string Get(string name) => Environment.GetEnvironmentVariable(name)?.Trim() ?? "";
        bool Passed(string name) => bool.TryParse(Environment.GetEnvironmentVariable(name), out bool value) && value;
        var authorization = new ExperimentalPaperAuthorization(
            Get("QUANTDESK_EXPERIMENT_ID"), Get("QUANTDESK_HYPOTHESIS_ID"),
            Get("QUANTDESK_STRATEGY_VERSION"), symbols, ParseRegisteredAt(), Get("QUANTDESK_EVIDENCE_REFERENCE"),
            Passed("QUANTDESK_LEAKAGE_SANITY_PASSED"), Passed("QUANTDESK_REPLAY_SANITY_PASSED"));
        // Every symbol, not just the first: an instrument the declaration does not name is one the
        // experiment was never registered to trade.
        foreach (string symbol in symbols)
        {
            if (!authorization.IsValidFor(symbol))
                throw new InvalidOperationException(
                    $"Experimental paper authorization does not cover '{symbol}', or failed its sanity checks.");
        }

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
