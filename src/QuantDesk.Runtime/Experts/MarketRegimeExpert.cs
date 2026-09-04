using QuantDesk.Domain.Forecasts;
using QuantDesk.Domain.Trading;
using QuantDesk.Domain.Numerics;
using QuantDesk.Runtime.Indicators;
using QuantDesk.Runtime.Scoring;

namespace QuantDesk.Runtime.Experts;

/// <summary>
/// Classifies the current market into regime probabilities from volatility and trend strength.
///
/// What this is, and what it is not
/// --------------------------------
/// The handbook specifies an HMM with a deterministic baseline beside it. This is the baseline. It
/// is a transparent function of two things the indicator set already computes -- how volatile the
/// market is against its own recent history, and how strongly it is trending -- and it exists
/// because a regime forecast that is merely honest is worth more today than an HMM that is not yet
/// fitted, validated or governed.
///
/// It should be replaced. When Python publishes a fitted HMM artifact with its degenerate-state
/// checks passed, this becomes the fallback rather than the answer, and the two will disagree in
/// ways worth reading. Until then a baseline that anyone can check by hand beats a latent-state
/// model nobody has validated.
///
/// Why regime matters more than it looks
/// -------------------------------------
/// Two rules in the exit engine were written against regime and could not be implemented, because
/// the family was declared and never emitted -- a management plan that said ExitOnRegimeChange with
/// nothing able to tell it the regime had changed. Section 12.2 also scores every expert on context
/// fit, which needs a context to fit against. This is that context.
///
/// Context only, never direction
/// -----------------------------
/// A regime is not a trade. Stress does not mean sell, and low-volatility trend does not mean buy;
/// they mean size differently, hold differently, and trust a directional forecast more or less. The
/// typed committee keeps the families apart so that this can never be read as an order.
/// </summary>
public sealed class MarketRegimeExpert(IForecastCalibrationSource? calibration = null)
{
    /// <summary>Bars of history the volatility percentile is measured against.</summary>
    public const int VolatilityLookback = 288;

    /// <summary>ADX above which a market is considered to be trending rather than ranging.</summary>
    public const double TrendingAdx = 25d;

    /// <summary>
    /// Volatility percentile above which the market is treated as stressed rather than merely
    /// volatile. Set high because stress should be rare: a regime that fires often is a description
    /// of the instrument, not of the moment.
    /// </summary>
    public const double StressPercentile = 0.95d;

    /// <param name="symbol">
    /// Which instrument this is about. It selects the measured calibration: a regime classifier's
    /// record on continuously-traded crypto says nothing about an equity ETF with an opening
    /// auction and a close.
    /// </param>
    public RegimeForecast? Forecast(
        IndicatorSet indicators,
        string symbol,
        int instrumentSlot,
        int expertId,
        TimeSpan horizon,
        long eventNs,
        long nowMonotonicTicks,
        long validUntilMonotonicTicks,
        long sourceStateVersion)
    {
        ArgumentNullException.ThrowIfNull(indicators);

        int last = indicators.Length - 1;
        if (last < VolatilityLookback) return null;
        if (!indicators.IsReadyAt(last, indicators.Atr14, indicators.Adx14)) return null;

        double atr = indicators.Atr14[last];
        double close = indicators.Close[last];
        if (!double.IsFinite(atr) || atr <= 0d || close <= 0d) return null;

        double adx = indicators.Adx14[last];
        if (!double.IsFinite(adx)) return null;

        // Volatility relative to this instrument's own recent history, not to an absolute level.
        // A 3% daily range is calm for one instrument and a crisis for another, so an absolute
        // threshold would classify the instrument rather than the regime.
        double percentile = VolatilityPercentile(indicators, last, atr / close);
        if (!double.IsFinite(percentile)) return null;

        // Trend strength and volatility level are close to independent, so the four ordinary
        // regimes fall out of their combination and stress is carved off the top of volatility.
        double trending = Math.Clamp((adx - TrendingAdx) / TrendingAdx, 0d, 1d);
        double volatile_ = Math.Clamp(percentile, 0d, 1d);

        double stress = percentile >= StressPercentile
            ? Math.Clamp((percentile - StressPercentile) / (1d - StressPercentile), 0d, 1d)
            : 0d;

        double remaining = Math.Max(1d - stress, 0d);
        double lowVolTrend = remaining * trending * (1d - volatile_);
        double highVolTrend = remaining * trending * volatile_;
        double range = remaining * (1d - trending);

        // No event model exists. Publishing a fabricated event probability would be worse than
        // publishing none, so it is explicitly zero and the family stays honest about the gap.
        const double EventProbability = 0d;

        double total = lowVolTrend + highVolTrend + range + stress + EventProbability;
        if (total <= 0d) return null;

        return new RegimeForecast(
            new ForecastMetadata(
                expertId, instrumentSlot, ForecastType.Regime, horizon,
                eventNs, nowMonotonicTicks, validUntilMonotonicTicks, sourceStateVersion,
                ModelVersion: 1, ForecastStatus.Valid),
            new Probability(lowVolTrend / total),
            new Probability(highVolTrend / total),
            new Probability(range / total),
            new Probability(stress / total),
            new Probability(EventProbability / total),

            // A deterministic baseline that has never been scored against realised regimes. Half is
            // the value that claims nothing, which is the correct claim to make.
            // Measured Brier from the scorer, or the unmeasured default until enough independent
            // episodes exist to say anything. A regime classifier that is confident and wrong ends
            // positions early, which is a cost that never shows up as a bad entry.
            CalibrationScore: calibration?.For(
                                  expertId, ForecastType.Regime, SymbolAssetClass.Of(symbol))
                ?? ForecastCalibration.Unmeasured);
    }

    /// <summary>
    /// Where the current normalised true range sits in its own recent distribution.
    ///
    /// Normalised by price so the comparison is scale-free, and measured against the instrument's
    /// own history so the answer describes the moment rather than the instrument.
    /// </summary>
    private static double VolatilityPercentile(IndicatorSet indicators, int last, double current)
    {
        int first = last - VolatilityLookback + 1;
        if (first <= 0) return double.NaN;

        int below = 0;
        int counted = 0;
        for (int i = first; i <= last; i++)
        {
            double atr = indicators.Atr14[i];
            double close = indicators.Close[i];
            if (!double.IsFinite(atr) || atr <= 0d || close <= 0d) continue;

            counted++;
            if (atr / close < current) below++;
        }

        return counted >= VolatilityLookback / 2 ? (double)below / counted : double.NaN;
    }
}
