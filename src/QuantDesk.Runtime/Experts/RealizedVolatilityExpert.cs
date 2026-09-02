using QuantDesk.Domain.Forecasts;
using QuantDesk.Runtime.Indicators;

namespace QuantDesk.Runtime.Experts;

/// <summary>
/// Forecasts future realized variance from the variance already observed, at three horizons.
///
/// The hypothesis
/// --------------
/// Volatility clusters. A quiet hour is more likely than not followed by a quiet hour, and a violent
/// one by another violent one, far more reliably than direction persists. This is the most robust
/// regularity in the whole of empirical finance and the reason a volatility forecast is worth
/// publishing even where a directional one is not: it is close to the only thing here that has a
/// right to expect to work.
///
/// Why HAR rather than a single EWMA
/// ---------------------------------
/// The heterogeneous autoregressive form -- short, medium and long realized variance combined --
/// exists because market participants act on different horizons and their volatilities are not the
/// same process observed at different speeds. A single decay has to choose one of those horizons
/// and be wrong about the others. Corsi's result is that the simple linear combination beats far
/// more elaborate models out of sample, which is the sort of finding worth taking at face value.
///
/// The coefficients here are the conventional ones, not fitted. Nothing in this system has fitted
/// them, and putting fitted-looking numbers in without the fit is how a model acquires unearned
/// authority. Python owns fitting; when it publishes a HAR artifact this expert should read it and
/// say which it used.
///
/// What it must never be
/// ---------------------
/// A volatility forecast is not a direction. Section 10.1 rejects a universal score precisely so
/// that this cannot quietly become one: high expected variance is a reason to size smaller and
/// widen a stop, never a reason to buy or sell. The typed committee keeps the two apart by
/// construction, and this expert publishes into the volatility family only.
/// </summary>
public sealed class RealizedVolatilityExpert(HarVarianceModel? fitted = null)
{
    private readonly HarVarianceModel _fitted = fitted ?? HarVarianceModel.Unfitted();

    /// <summary>True when a validated Python artifact is driving the forecast.</summary>
    public bool IsFitted => _fitted.IsFitted;

    /// <summary>Bars in the short, medium and long HAR components at five-minute sampling.</summary>
    public const int ShortBars = 12;
    public const int MediumBars = 60;
    public const int LongBars = 288;

    /// <summary>
    /// Conventional HAR weights on the daily, weekly and monthly components, rescaled to the three
    /// horizons this system samples at. Not fitted, and deliberately not presented as though it is.
    /// </summary>
    private const double ShortWeight = 0.35d;
    private const double MediumWeight = 0.35d;
    private const double LongWeight = 0.30d;

    /// <summary>Five-minute bars in a 365-day year, for annualising.</summary>
    private const double BarsPerYear = 288d * 365d;

    /// <summary>
    /// A variance forecast, or null when there is not enough history for the long component.
    ///
    /// Refusing is the honest answer rather than falling back to whatever is available. A HAR
    /// forecast built from a short window is not a less precise HAR forecast, it is a different
    /// model with the same name, and section 9.4 is explicit that missing history is not to be
    /// encoded as a value.
    /// </summary>
    public VolatilityForecast? Forecast(
        IndicatorSet indicators,
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
        if (last < LongBars) return null;

        double shortRun = RealizedVariance(indicators.Close, last, ShortBars);
        double medium = RealizedVariance(indicators.Close, last, MediumBars);
        double longRun = RealizedVariance(indicators.Close, last, LongBars);
        if (!double.IsFinite(shortRun) || !double.IsFinite(medium) || !double.IsFinite(longRun))
            return null;

        // A fitted artifact if one has been validated, the conventional weights otherwise -- and
        // never a silent blend of the two. The fallback is the same combination this expert has
        // always used and is documented as unfitted; what changes with an artifact is that the
        // coefficients came from data rather than from convention.
        double expected = _fitted.Predict(shortRun, medium, longRun)
            ?? (ShortWeight * shortRun) + (MediumWeight * medium) + (LongWeight * longRun);
        if (!double.IsFinite(expected) || expected < 0d) return null;

        // Dispersion across the three components is the honest uncertainty: when short, medium and
        // long disagree the regime is turning and the point forecast deserves less trust. Inventing
        // a tighter interval than the components support is how a forecast becomes overconfident.
        double mean = (shortRun + medium + longRun) / 3d;
        double spread =
            (((shortRun - mean) * (shortRun - mean))
             + ((medium - mean) * (medium - mean))
             + ((longRun - mean) * (longRun - mean))) / 3d;

        return new VolatilityForecast(
            new ForecastMetadata(
                expertId, instrumentSlot, ForecastType.RealizedVolatility, horizon,
                eventNs, nowMonotonicTicks, validUntilMonotonicTicks, sourceStateVersion,
                ModelVersion: 1, ForecastStatus.Valid),
            ExpectedRealizedVariance: expected,
            ExpectedAnnualizedVolatility: Math.Sqrt(Math.Max(expected, 0d) * BarsPerYear),
            ForecastVariance: spread,

            // Unscored until it has been scored, whether or not the coefficients were fitted. A
            // fitted model is not a calibrated one: the fit says the coefficients came from data,
            // the calibration would say the resulting forecasts were checked against outcomes, and
            // only the second is a claim about being right. The scorer now measures exactly that
            // and its QLIKE is the number that should eventually replace this.
            CalibrationScore: 0.5d);
    }

    /// <summary>
    /// Mean squared log return over the last <paramref name="bars"/> observations.
    ///
    /// Log returns rather than simple ones so the measure is additive across horizons, which is the
    /// property the HAR combination depends on.
    /// </summary>
    private static double RealizedVariance(double[] closes, int last, int bars)
    {
        int first = last - bars + 1;
        if (first <= 0) return double.NaN;

        double sum = 0d;
        int counted = 0;
        for (int i = first; i <= last; i++)
        {
            double previous = closes[i - 1];
            double current = closes[i];
            if (previous <= 0d || current <= 0d) continue;

            double logReturn = Math.Log(current / previous);
            if (!double.IsFinite(logReturn)) continue;

            sum += logReturn * logReturn;
            counted++;
        }

        // A window that is mostly unusable is not a thin estimate, it is a different window.
        return counted >= bars / 2 && counted > 0 ? sum / counted : double.NaN;
    }
}
