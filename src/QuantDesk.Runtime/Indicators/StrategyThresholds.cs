using System.Globalization;

namespace QuantDesk.Runtime.Indicators;

/// <summary>
/// The numbers inside the entry rules, named and overridable rather than buried in expressions.
///
/// Why these were a problem
/// ------------------------
/// The constitution's rule is that no magic threshold hides in source and that runtime behaviour is
/// versioned configuration. Risk limits were moved out to configuration months ago; the strategy
/// layer never was, so ADX above 25, RSI crossing 30, Stochastic under 30, volume two deviations
/// out and a VWAP gap of 1.5 ATRs sat as literals in the middle of boolean expressions. Every one
/// is a tuning knob that was set once by whoever wrote the rule, is invisible to an operator, and
/// cannot be swept, versioned, or even listed without reading the code.
///
/// The trap that comes with fixing it
/// ----------------------------------
/// A threshold is not a setting like a timeout. Changing it changes what the rule *is*, and every
/// measured figure in the registry describes the rule at the values below. Sweeping RSI from 30 to
/// 40 does not produce a better-tuned <c>reversion.rsi-oversold.v1</c>; it produces a different
/// rule wearing the same name and the same out-of-sample statistics, which is precisely the
/// staleness that took a full re-measurement to detect this morning.
///
/// So overriding is allowed and is recorded. <see cref="IsDefault"/> is false the moment any value
/// differs, and the registry marks every rule that reads a changed threshold as Stale -- unmeasured
/// rather than unproven, and therefore not tradable until it is measured again. That makes the
/// sweep possible without letting it quietly invalidate the evidence.
/// </summary>
/// <param name="AdxTrendFloor">Directional-index level above which a trend is considered established.</param>
/// <param name="RsiOversoldLevel">RSI level a reversion entry requires the market to cross back above.</param>
/// <param name="StochasticOversoldCeiling">Stochastic %K below which a bullish cross is treated as oversold.</param>
/// <param name="VolumeSurgeDeviations">Standard deviations above the time-of-day baseline that count as a surge.</param>
/// <param name="VwapGapAtrs">How far below VWAP, in ATRs, a displacement must be to be worth fading.</param>
/// <param name="RsiTrendFloor">RSI level above which volume-confirmed trend treats momentum as intact.</param>
public sealed record StrategyThresholds(
    double AdxTrendFloor = 25d,
    double RsiOversoldLevel = 30d,
    double StochasticOversoldCeiling = 30d,
    double VolumeSurgeDeviations = 2d,
    double VwapGapAtrs = 1.5d,
    double RsiTrendFloor = 50d)
{
    /// <summary>The values every figure in the strategy registry was measured against.</summary>
    public static readonly StrategyThresholds Measured = new();

    /// <summary>True when nothing has been moved away from what the research measured.</summary>
    public bool IsDefault => this == Measured;

    /// <summary>
    /// Reads overrides from the environment, falling back to what the research measured.
    ///
    /// Environment rather than a settings file because that is how the rest of the runtime's
    /// configuration already arrives, and because an override is meant to be a deliberate act for
    /// one run rather than a change that persists silently into the next.
    /// </summary>
    public static StrategyThresholds FromEnvironment() => new(
        Number("QUANTDESK_STRATEGY_ADX_TREND_FLOOR", Measured.AdxTrendFloor),
        Number("QUANTDESK_STRATEGY_RSI_OVERSOLD", Measured.RsiOversoldLevel),
        Number("QUANTDESK_STRATEGY_STOCHASTIC_OVERSOLD", Measured.StochasticOversoldCeiling),
        Number("QUANTDESK_STRATEGY_VOLUME_SURGE_SIGMA", Measured.VolumeSurgeDeviations),
        Number("QUANTDESK_STRATEGY_VWAP_GAP_ATRS", Measured.VwapGapAtrs),
        Number("QUANTDESK_STRATEGY_RSI_TREND_FLOOR", Measured.RsiTrendFloor));

    /// <summary>
    /// Which rules read a threshold that has been moved.
    ///
    /// Listed per rule rather than as one flag, so moving the ADX floor does not invalidate the
    /// evidence for a Bollinger rule that never reads it.
    /// </summary>
    public IReadOnlySet<string> RulesInvalidatedBy()
    {
        HashSet<string> invalidated = new(StringComparer.Ordinal);

        if (AdxTrendFloor != Measured.AdxTrendFloor) invalidated.Add("trend.adx-filtered.v1");
        if (RsiOversoldLevel != Measured.RsiOversoldLevel) invalidated.Add("reversion.rsi-oversold.v1");
        if (StochasticOversoldCeiling != Measured.StochasticOversoldCeiling)
            invalidated.Add("reversion.stochastic-oversold.v1");
        if (VolumeSurgeDeviations != Measured.VolumeSurgeDeviations)
            invalidated.Add("volume.surge-breakout.v1");
        if (VwapGapAtrs != Measured.VwapGapAtrs) invalidated.Add("reversion.vwap.v1");
        if (RsiTrendFloor != Measured.RsiTrendFloor) invalidated.Add("volume.obv-confirmed-trend.v1");

        return invalidated;
    }

    private static double Number(string variable, double fallback) =>
        double.TryParse(
            Environment.GetEnvironmentVariable(variable),
            NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value)
            ? value
            : fallback;
}
