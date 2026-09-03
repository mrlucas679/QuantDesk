using System.Collections.Concurrent;
using QuantDesk.Domain.Forecasts;
using QuantDesk.Runtime.Execution;
using QuantDesk.Runtime.Experts;
using QuantDesk.Runtime.Indicators;
using QuantDesk.Runtime.Scoring;
using QuantDesk.Runtime.Time;
using QuantDesk.Domain.Experts;

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
    TypedForecastCommittee committee,
    MeasuredCalibrationSource calibrationSource,
    IRuntimeClock clock,
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

        RegimeForecast? produced = expert.Forecast(
            indicators, symbol, instrumentSlot, ExpertId, Horizon,
            eventNs, nowMonotonicTicks, nowMonotonicTicks + HorizonTicks, sourceStateVersion);

        // Through the committee, not straight to the caller.
        //
        // Section 10.1 keeps forecast families apart so a volatility reading can never become a
        // direction, and the typed committee is where that separation is enforced -- along with the
        // staleness, availability and state-version checks a single expert's output does not carry.
        // This path used to publish whatever the expert returned, so a forecast whose validity had
        // expired was as good as a fresh one and nothing said otherwise.
        //
        // One expert per family today, so aggregation is a pass-through. The gates are not.
        ForecastFamilyDecision<RegimeForecast> decision = produced is { } vote
            ? committee.EvaluateRegime(
                instrumentSlot, [new ForecastVote<RegimeForecast>(ExpertId, vote, 1d)],
                nowMonotonicTicks, sourceStateVersion, expectedExperts: 1)
            : committee.EvaluateRegime(
                instrumentSlot, [], nowMonotonicTicks, sourceStateVersion, expectedExperts: 1);

        RegimeForecast? forecast = decision.HasForecast ? decision.Forecast : null;

        // A refusal leaves the previous classification standing rather than clearing it. The
        // alternative -- forgetting the regime whenever a bar is missing -- would make the exit
        // rule fire and stop firing with the feed rather than with the market.
        if (forecast is { } regime) _regimes[symbol] = regime.MostLikely;

        // Recorded from the expert's own output, before the committee sees it.
        //
        // Scoring and deciding are different uses. Gating the recording would mean a badly
        // calibrated expert stops producing the outcomes that would show it improving, so it
        // could never earn its way back -- the trap the shadow signal log exists to avoid for
        // rules. What the committee gates is what informs a decision.
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

        VolatilityForecast? produced = volatility.Forecast(
            indicators, symbol, instrumentSlot, VolatilityExpertId, Horizon,
            eventNs, nowMonotonicTicks, nowMonotonicTicks + HorizonTicks, sourceStateVersion);

        // Same gate, same reason. A variance recorded as an outcome that was already stale when it
        // was published would score the expert on a forecast it had withdrawn.
        ForecastFamilyDecision<VolatilityForecast> decision = produced is { } vote
            ? committee.EvaluateVolatility(
                instrumentSlot, [new ForecastVote<VolatilityForecast>(VolatilityExpertId, vote, 1d)],
                nowMonotonicTicks, sourceStateVersion, expectedExperts: 1)
            : committee.EvaluateVolatility(
                instrumentSlot, [], nowMonotonicTicks, sourceStateVersion, expectedExperts: 1);

        VolatilityForecast? forecast = decision.HasForecast ? decision.Forecast : null;
        if (forecast is not { } published) return;

        int last = indicators.Length - 1;
        DateTimeOffset firedAt = indicators.HasTimeAxis ? indicators.Timestamps[last] : clock.UtcNow;

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

        int resolved = outcomes.Resolve(clock.UtcNow, (candidate, family) =>
            family is ForecastType.RealizedVolatility
            && string.Equals(candidate, symbol, StringComparison.OrdinalIgnoreCase)
                ? RealisedVarianceOverHorizon(indicators)
                : null);

        // Rescored only when something actually resolved. Scoring walks every resolved outcome, so
        // doing it on every observation would put a full pass over the log inside the decision
        // path -- and it would produce the same answer, because nothing new had been measured.
        if (resolved > 0) calibrationSource.Refresh(outcomes.Scores());
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

    /// <summary>
    /// The horizon in the clock's monotonic units.
    ///
    /// An instance member rather than a static one, because there is no scale to compute it on
    /// until a clock says which. As a static it was fixed to Stopwatch.Frequency, which is right
    /// for the live clock and out by a factor of a hundred for a virtual one on Linux.
    /// </summary>
    private long HorizonTicks => clock.MonotonicTicksFor(Horizon);
}
