using QuantDesk.Domain.Contracts;
using QuantDesk.Domain.Forecasts;
using QuantDesk.Runtime.Indicators;
using QuantDesk.Runtime.Research;
using QuantDesk.Runtime.Scoring;

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
public sealed class RealizedVolatilityExpert(
    IFittedModelSource? models = null,
    IForecastCalibrationSource? calibration = null)
{
    /// <summary>
    /// Read on every forecast, not captured at construction.
    ///
    /// This expert is a singleton built when the host starts; the artifacts arrive later and are
    /// replaced while the process runs. Holding the model from construction meant holding whatever
    /// existed at boot -- which on a fresh volume is nothing, and is precisely why this expert has
    /// been registered, reachable and permanently unfitted.
    /// </summary>
    /// <remarks>
    /// Keyed by the instrument being forecast. There used to be one HAR for the whole runtime, and
    /// it was fitted on BTC/USD -- so every SPY, QQQ, IWM and DIA variance forecast came from
    /// Bitcoin's coefficients. Nothing detected it: the schema hash matched, the parity cases
    /// reproduced, and the artifact never said what it was fitted on.
    /// </remarks>
    private HarVarianceModel Fitted(string symbol) =>
        models?.Har(symbol, FeatureContract.BarDurationMinutes) ?? HarVarianceModel.Unfitted();

    /// <summary>
    /// The second estimate of the same quantity, read the same way and for the same reason.
    ///
    /// GARCH was fitted, parity-checked against <c>arch</c>, adopted on every cycle, and consulted
    /// by nothing. A model that is verified and then ignored is not a safeguard; it is an artifact
    /// with a passing test beside it.
    /// </summary>
    private GarchVarianceModel FittedGarch(string symbol) =>
        models?.Garch(symbol, FeatureContract.BarDurationMinutes) ?? GarchVarianceModel.Unfitted();

    /// <summary>True when a validated Python artifact fitted on this instrument drives its forecast.</summary>
    public bool IsFittedFor(string symbol) => Fitted(symbol).IsFitted;

    /// <summary>
    /// Squared percent return to squared log return.
    ///
    /// GARCH is fitted on percent returns -- the artifact says so in
    /// <c>variant.return_units</c> and again in <c>feature_semantics.units</c>, which reads
    /// <c>squared_percent_return</c> -- while everything here is in mean squared log return. That
    /// is a factor of ten thousand, and it is the difference between two models disagreeing and two
    /// models being quoted in different currencies. Left unconverted, GARCH would appear to
    /// disagree with HAR by four orders of magnitude on every bar, and the disagreement term below
    /// would report permanent maximum uncertainty while looking like it was measuring something.
    /// </summary>
    private const double SquaredPercentToSquaredLogReturn = 1e-4;

    /// <summary>
    /// What this expert computes, so a fitted model can be checked against it rather than believed.
    ///
    /// The schema hash is derived from this rather than read out of the artifact. Comparing an
    /// artifact's hash to its own hash passes for every artifact, including one fitted on a
    /// different feature set -- which is the failure the hash exists to prevent.
    /// </summary>
    public static RuntimeFeatureContract FeatureContract { get; } = new(
        FeatureSchemaDigest.Compute(
            schemaVersion: "har-realised-variance-v1",
            featureNames: HarVarianceModel.FeatureNames,
            dtypes: HarVarianceModel.FeatureNames.ToDictionary(name => name, _ => "float64"),
            lookbackPeriods: LongBars,
            sourceRequirements: ["alpaca_ohlcv"]),
        HarVarianceModel.FeatureNames.ToDictionary(name => name, _ => VarianceUnits),
        MissingPolicy,
        BarDurationMinutes);

    /// <summary>
    /// Mean squared log return, which is what <see cref="RealizedVariance"/> computes.
    ///
    /// Stated because the schema hash does not cover it. Two models can agree on the names, the
    /// order and the warm-up while one was fitted on squared returns and the other on a rolling
    /// standard deviation, and nothing about either forecast would look wrong.
    /// </summary>
    public const string VarianceUnits = "mean_squared_log_return";

    /// <summary>A missing bar is refused rather than interpolated: a gap is not a quiet market.</summary>
    public const string MissingPolicy = "refuse";

    /// <summary>The bar these windows are counted in.</summary>
    public const int BarDurationMinutes = 5;

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
    /// <param name="symbol">
    /// Which instrument this forecast is about. Required, not incidental: it selects the fitted
    /// model, and a model fitted on something else is refused rather than substituted.
    /// </param>
    public VolatilityForecast? Forecast(
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
        double expected = Fitted(symbol).Predict(shortRun, medium, longRun)
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

        // A second, independent estimate of the same quantity -- and its disagreement with the
        // first is better evidence about this forecast's uncertainty than the dispersion of one
        // model's own inputs, because the two models fail in different ways. HAR is a linear
        // combination of realised variance at three horizons; GARCH is a recursion on the last
        // shock and the last variance. When they agree, the number is worth more.
        //
        // It is added to the uncertainty and never to the point forecast. Blending them would make
        // a third model nobody fitted, validated or parity-checked -- the same objection the
        // fallback above is careful about, where a fitted HAR is used or the conventional weights
        // are used, never a quiet mixture. And a variance forecast is advisory in any case: it
        // sizes a position or ends one early, and section 10.1 keeps it from ever becoming a
        // direction.
        if (GarchVariance(symbol, indicators.Close, last) is { } garch)
        {
            double disagreement = (expected - garch) * (expected - garch);
            spread += disagreement;
        }

        return new VolatilityForecast(
            new ForecastMetadata(
                expertId, instrumentSlot, ForecastType.RealizedVolatility, horizon,
                eventNs, nowMonotonicTicks, validUntilMonotonicTicks, sourceStateVersion,
                ModelVersion: 1, ForecastStatus.Valid),
            ExpectedRealizedVariance: expected,
            ExpectedAnnualizedVolatility: Math.Sqrt(Math.Max(expected, 0d) * BarsPerYear),
            ForecastVariance: spread,

            // The scorer's measured QLIKE, mapped through ForecastCalibration, or the
            // unmeasured default until there is enough independent evidence to say anything. A
            // fitted model is still not a calibrated one -- the fit says the coefficients came from
            // data, this says the resulting forecasts were checked against what happened -- and
            // only the second is a claim about being right.
            CalibrationScore: calibration?.For(expertId, ForecastType.RealizedVolatility)
                ?? ForecastCalibration.Unmeasured);
    }

    /// <summary>
    /// Mean squared log return over the last <paramref name="bars"/> observations.
    ///
    /// Log returns rather than simple ones so the measure is additive across horizons, which is the
    /// property the HAR combination depends on.
    /// </summary>
    /// <summary>
    /// GARCH's conditional variance for the next bar, in this expert's units, or nothing.
    ///
    /// Nothing, in three cases, each of which is a refusal rather than a zero: no artifact has been
    /// adopted; the artifact was fitted on a return scale this does not know how to convert; or
    /// there is less history than the model's warm-up requires. The warm-up is 289 bars against
    /// HAR's 288, so on exactly one bar of history HAR answers and GARCH does not -- and the
    /// forecast is then simply the one it always was.
    /// </summary>
    private double? GarchVariance(string symbol, double[] closes, int last)
    {
        GarchVarianceModel garch = FittedGarch(symbol);
        if (!garch.IsFitted) return null;

        // Refuse rather than guess. A model fitted on a scale nobody has mapped is not evidence
        // about this instrument, and assuming it matched would be the units error this whole
        // conversion exists to avoid.
        if (!string.Equals(garch.ReturnUnits, "percent", StringComparison.Ordinal)) return null;

        int required = garch.WarmupBars;
        if (required <= 0 || last < required) return null;

        var squaredPercentReturns = new List<double>(required);
        for (int i = last - required + 1; i <= last; i++)
        {
            double previous = closes[i - 1];
            double current = closes[i];
            if (previous <= 0d || current <= 0d) return null;

            double percentReturn = Math.Log(current / previous) * 100d;
            if (!double.IsFinite(percentReturn)) return null;

            squaredPercentReturns.Add(percentReturn * percentReturn);
        }

        double? warmed = garch.WarmedVariance(squaredPercentReturns);
        if (warmed is not { } variance) return null;

        // One step forward, so both models are answering the same question: what is the variance of
        // the next bar. The warmed figure is the variance of the bar just seen.
        double? predicted = garch.Predict(squaredPercentReturns[^1], variance);
        return predicted is { } next && double.IsFinite(next)
            ? next * SquaredPercentToSquaredLogReturn
            : null;
    }

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
