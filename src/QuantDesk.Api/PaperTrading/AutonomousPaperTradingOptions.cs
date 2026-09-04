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
    string LaneName,
    bool Enabled,
    AutonomousTradingMode Mode,
    OpportunityExpression Expression,
    ExperimentalPaperAuthorization? ExperimentalAuthorization,
    IReadOnlyList<string> Symbols,
    decimal OrderNotional,
    TimeSpan HoldDuration,
    TimeSpan FillTimeout,
    TimeSpan CycleInterval,
    int ExplorationAllowance = 0)
{
    /// <summary>
    /// How many concurrent positions may be opened in rules that are measured to lose.
    ///
    /// Zero by default, which is the honest resting state: after the 2026-09-02 re-measurement no
    /// rule in either book has a positive expected edge at the sixty basis points the venue
    /// charges, so nothing qualifies to trade and the desk stands down.
    ///
    /// Set above zero and the desk buys evidence instead. That is a real decision with a known
    /// price, not a loosened gate: the known-loser test still refuses every one of these rules, and
    /// the risk governor still records the admission as ApprovedAsExploration rather than folding
    /// it into an ordinary approval. What changes is only that a bounded number of such positions
    /// is permitted at once, spent on the rules closest to viable.
    ///
    /// The price is roughly the gap between what the venue charges and what the best rule is
    /// measured to earn -- about 45 bps a round trip as of that measurement, or some 90 cents on a
    /// 200-dollar position. Two concurrent positions is a few dollars a day. That buys live
    /// out-of-sample evidence on rules whose only alternative is never being heard from again.
    /// </summary>
    public bool ExplorationEnabled => ExplorationAllowance > 0;
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

    /// <summary>
    /// One lane per asset class, because they are not the same instrument.
    ///
    /// Crypto trades continuously and pays roughly 68 bps a round trip; US equities trade for six
    /// and a half hours and pay about three. They want different notionals, different holding
    /// periods, and different admission hurdles, and a single lane would have to average all of
    /// that into one setting. Separate lanes also fail separately: an equity feed outage or a
    /// closed session says nothing about whether crypto should keep trading.
    /// </summary>
    public static IReadOnlyList<AutonomousPaperTradingOptions> AllLanes(PaperTradingOptions trading)
    {
        var lanes = new List<AutonomousPaperTradingOptions>();

        AutonomousPaperTradingOptions crypto = FromEnvironment(trading);
        if (crypto.Enabled && crypto.Symbols.Count > 0) lanes.Add(crypto);

        AutonomousPaperTradingOptions? equity = EquityLane(trading, crypto);
        if (equity is not null) lanes.Add(equity);

        // A disabled configuration still yields one lane, so the rest of the graph has options to
        // resolve and the status endpoint has something to report.
        return lanes.Count > 0 ? lanes : [crypto];
    }

    /// <summary>
    /// The equity lane, or null when none is configured.
    ///
    /// It inherits the crypto lane's mode and authorization deliberately: both run under the same
    /// experimental declaration, and letting them drift apart would mean one lane trading under a
    /// registration the other had passed.
    /// </summary>
    private static AutonomousPaperTradingOptions? EquityLane(
        PaperTradingOptions trading, AutonomousPaperTradingOptions crypto)
    {
        string[] symbols =
        [
            .. (Environment.GetEnvironmentVariable("QUANTDESK_AUTONOMOUS_EQUITY_SYMBOL") ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(item => item.ToUpperInvariant())
                .Distinct(StringComparer.Ordinal),
        ];
        if (symbols.Length == 0 || !crypto.Enabled) return null;

        foreach (string symbol in symbols)
        {
            if (!trading.Symbols.Values.Contains(symbol, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"QUANTDESK_AUTONOMOUS_EQUITY_SYMBOL '{symbol}' must be present in QUANTDESK_SYMBOLS.");
        }

        decimal notional = ParsePositiveDecimal("QUANTDESK_AUTONOMOUS_EQUITY_ORDER_NOTIONAL", crypto.OrderNotional);
        if (notional > trading.MaximumOrderNotional)
            throw new InvalidOperationException("Equity order notional exceeds the configured paper-order limit.");
        int holdingHours = ParsePositiveInteger("QUANTDESK_AUTONOMOUS_EQUITY_MAX_HOLDING_HOURS", 2);

        ExperimentalPaperAuthorization? authorization = crypto.Mode == AutonomousTradingMode.ExperimentalPaper
            ? ReadExperimentalAuthorization(symbols)
            : null;

        return new(
            "equity", true, crypto.Mode, OpportunityExpression.Spot, authorization, symbols,
            notional, TimeSpan.FromHours(holdingHours), crypto.FillTimeout, crypto.CycleInterval,
            ParseAllowance("QUANTDESK_AUTONOMOUS_EQUITY_EXPLORATION_ALLOWANCE"));
    }

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
            "crypto",
            enabled,
            mode,
            ParseExpression(),
            authorization,
            symbols,
            notional,
            TimeSpan.FromHours(maximumHoldingHours),
            TimeSpan.FromSeconds(fillTimeoutSeconds),
            TimeSpan.FromSeconds(cycleIntervalSeconds),
            ParseAllowance("QUANTDESK_AUTONOMOUS_EXPLORATION_ALLOWANCE"));
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
            if (!authorization.IsValidFor(symbol, DateTimeOffset.UtcNow))
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

    /// <summary>Non-negative, because zero is the meaningful default rather than an error.</summary>
    private static int ParseAllowance(string name)
    {
        string text = Environment.GetEnvironmentVariable(name) ?? "0";
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value >= 0
            ? value
            : throw new InvalidOperationException($"{name} must be a non-negative integer value.");
    }

    private static int ParsePositiveInteger(string name, int fallback)
    {
        string text = Environment.GetEnvironmentVariable(name) ?? fallback.ToString(CultureInfo.InvariantCulture);
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value > 0
            ? value
            : throw new InvalidOperationException($"{name} must be a positive integer value.");
    }
}
