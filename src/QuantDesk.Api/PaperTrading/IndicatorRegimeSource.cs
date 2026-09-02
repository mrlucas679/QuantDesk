using System.Collections.Concurrent;
using QuantDesk.Domain.Forecasts;
using QuantDesk.Runtime.Execution;
using QuantDesk.Runtime.Experts;
using QuantDesk.Runtime.Indicators;
using QuantDesk.Runtime.Scoring;

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
public sealed class IndicatorRegimeSource(
    MarketRegimeExpert expert,
    RealizedVolatilityExpert volatility,
    ForecastOutcomeLog? outcomes = null) : IRegimeSource
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

        RecordVolatilityForecast(symbol, indicators, instrumentSlot, eventNs, nowMonotonicTicks, sourceStateVersion);
        ResolveDueForecasts(symbol, indicators);
    }

    /// <summary>
    /// Records the volatility forecast so it can be scored, and the realised variance so it can be
    /// scored against.
    ///
    /// The scorers existed with nothing feeding them, which is the shape of debt this system keeps
    /// producing: a measurement built, tested, and never given anything to measure. Volatility is
    /// the family where the loop closes cleanly on its own -- the expert predicts a variance, and
    /// the variance that then realises is computable from the same bars, with no fill, no cost
    /// model and no trade in between.
    ///
    /// The episode is the bar. Several experts asked about the same instrument at the same bar are
    /// describing one market realisation, which is what lets the scorer count independent evidence
    /// instead of observations.
    /// </summary>
    private void RecordVolatilityForecast(
        string symbol,
        IndicatorSet indicators,
        int instrumentSlot,
        long eventNs,
        long nowMonotonicTicks,
        long sourceStateVersion)
    {
        if (outcomes is null) return;

        VolatilityForecast? forecast = volatility.Forecast(
            indicators, instrumentSlot, VolatilityExpertId, Horizon,
            eventNs, nowMonotonicTicks, nowMonotonicTicks + HorizonTicks, sourceStateVersion);
        if (forecast is not { } published) return;

        int last = indicators.Length - 1;
        DateTimeOffset firedAt = indicators.HasTimeAxis ? indicators.Timestamps[last] : DateTimeOffset.UtcNow;

        outcomes.Record(
        [
            new ForecastOutcomeRecord(
                OutcomeId: $"{VolatilityExpertId}|{symbol}|{firedAt:yyyyMMddTHHmm}",
                EpisodeId: firedAt.ToUnixTimeSeconds(),
                ExpertId: VolatilityExpertId,
                ForecastType: ForecastType.RealizedVolatility,
                Symbol: symbol,
                Regime: CurrentRegime(symbol)?.ToString() ?? nameof(MarketRegime.Unknown),
                PredictedValue: published.ExpectedRealizedVariance,
                ResolveAt: firedAt.Add(Horizon)),
        ]);
    }

    /// <summary>
    /// Closes out any forecast whose horizon has passed, using the variance that actually realised.
    ///
    /// Measured over the same span the forecast covered, so the comparison is like for like. A
    /// forecast scored against a different window is not a less precise score, it is a score of
    /// something else.
    /// </summary>
    private void ResolveDueForecasts(string symbol, IndicatorSet indicators)
    {
        if (outcomes is null) return;

        outcomes.Resolve(DateTimeOffset.UtcNow, (candidate, family) =>
            family is ForecastType.RealizedVolatility
            && string.Equals(candidate, symbol, StringComparison.OrdinalIgnoreCase)
                ? RealisedVarianceOverHorizon(indicators)
                : null);
    }

    /// <summary>Mean squared log return over the bars the forecast horizon covered.</summary>
    private static double? RealisedVarianceOverHorizon(IndicatorSet indicators)
    {
        int bars = (int)(Horizon.TotalMinutes / 5d);
        int last = indicators.Length - 1;
        if (bars < 1 || last < bars) return null;

        double sum = 0d;
        int counted = 0;
        for (int i = last - bars + 1; i <= last; i++)
        {
            double previous = indicators.Close[i - 1];
            double current = indicators.Close[i];
            if (previous <= 0d || current <= 0d) continue;

            double logReturn = Math.Log(current / previous);
            if (!double.IsFinite(logReturn)) continue;

            sum += logReturn * logReturn;
            counted++;
        }

        return counted > 0 ? sum / counted : null;
    }

    public MarketRegime? CurrentRegime(string symbol) =>
        _regimes.TryGetValue(symbol, out MarketRegime regime) ? regime : null;

    /// <summary>Every symbol classified so far, for the status surface.</summary>
    public IReadOnlyDictionary<string, MarketRegime> Snapshot() =>
        new Dictionary<string, MarketRegime>(_regimes, StringComparer.OrdinalIgnoreCase);

    private const int ExpertId = 21;
    private const int VolatilityExpertId = 20;
    private static readonly TimeSpan Horizon = TimeSpan.FromMinutes(5);
    private static readonly long HorizonTicks = (long)(Horizon.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);
}
