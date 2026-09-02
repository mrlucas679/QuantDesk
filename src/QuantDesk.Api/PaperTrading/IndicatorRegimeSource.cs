using System.Collections.Concurrent;
using QuantDesk.Domain.Forecasts;
using QuantDesk.Runtime.Execution;
using QuantDesk.Runtime.Experts;
using QuantDesk.Runtime.Indicators;

namespace QuantDesk.Api.PaperTrading;

/// <summary>
/// Holds the most recent regime each symbol was classified into, for the rules that need context.
///
/// Written on the evaluation path, which already builds the indicator set the classifier reads, and
/// read by the exit engine on a different thread. That is the only reason this exists as a type
/// rather than a call: the regime is computed where the bars are and needed where the positions
/// are, and those are not the same place.
///
/// Bounded by the traded universe, which is fixed at startup, so the dictionary cannot grow without
/// limit -- the constitution forbids an unbounded collection and this is the shape that would
/// otherwise become one.
/// </summary>
public sealed class IndicatorRegimeSource(MarketRegimeExpert expert) : IRegimeSource
{
    private readonly ConcurrentDictionary<string, MarketRegime> _regimes =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Classifies and records the regime for a symbol, if the history supports one.</summary>
    public void Observe(
        string symbol,
        IndicatorSet indicators,
        int instrumentSlot,
        long eventNs,
        long nowMonotonicTicks,
        long sourceStateVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        ArgumentNullException.ThrowIfNull(indicators);

        RegimeForecast? forecast = expert.Forecast(
            indicators, instrumentSlot, ExpertId, Horizon,
            eventNs, nowMonotonicTicks, nowMonotonicTicks + HorizonTicks, sourceStateVersion);

        // A refusal leaves the previous classification standing rather than clearing it. The
        // alternative -- forgetting the regime whenever a bar is missing -- would make the exit
        // rule fire and stop firing with the feed rather than with the market.
        if (forecast is { } regime) _regimes[symbol] = regime.MostLikely;
    }

    public MarketRegime? CurrentRegime(string symbol) =>
        _regimes.TryGetValue(symbol, out MarketRegime regime) ? regime : null;

    /// <summary>Every symbol classified so far, for the status surface.</summary>
    public IReadOnlyDictionary<string, MarketRegime> Snapshot() =>
        new Dictionary<string, MarketRegime>(_regimes, StringComparer.OrdinalIgnoreCase);

    private const int ExpertId = 21;
    private static readonly TimeSpan Horizon = TimeSpan.FromMinutes(5);
    private static readonly long HorizonTicks = (long)(Horizon.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);
}
