namespace QuantDesk.Domain.Risk;

/// <summary>
/// How much notional a signal is worth, given how volatile the instrument is expected to be.
///
/// The handbook states it as <c>w = (sigma_target / sigma_hat) * s</c>, in the same formula library
/// as the momentum and mean-reversion definitions, and calls volatility management a strong risk
/// overlay in historical tests. This system had the pieces and never joined them: every order was a
/// flat notional, identical for BTC and for DIA, while HAR and GARCH variance models were fitted per
/// instrument, parity-checked, adopted on every cycle, and consulted by nothing that sized anything.
///
/// Why it matters here specifically
/// --------------------------------
/// A fixed notional does not mean fixed risk; it means risk proportional to whatever the instrument
/// happens to be doing. On 2026-09-03 the lane held $200 of AAVE and $200 of ETH against the same
/// $10 defined maximum loss, while AAVE was moving roughly twice as far per bar. The stop is
/// therefore twice as close in AAVE's own terms -- it exits on noise there and on nothing here --
/// and the two positions contribute quite different amounts to portfolio variance despite looking
/// identical in every record.
///
/// What this is not
/// ----------------
/// It is not an edge. Scaling by volatility changes the distribution of outcomes, not their mean,
/// and nothing here claims otherwise: a strategy with no edge sized well still has no edge. It
/// makes risk comparable across instruments, which is a precondition for measuring skill per
/// instrument rather than a substitute for having any.
/// </summary>
public static class VolatilityScaledSizing
{
    /// <summary>
    /// The widest and narrowest this may move a position from its base size.
    ///
    /// Bounded because the scale is a ratio of an estimate to a constant, and a variance forecast
    /// that collapses toward zero -- a quiet window, a warm-up artefact, a model fitted on a
    /// different regime -- would otherwise ask for an unbounded position. Section 20.3 treats a
    /// model's output as an input to a bounded decision, never as an instruction.
    /// </summary>
    public const double MinimumScale = 0.25d;

    /// <inheritdoc cref="MinimumScale"/>
    public const double MaximumScale = 2.0d;

    /// <summary>
    /// Notional for one position, scaled so its expected volatility contribution matches the target.
    /// </summary>
    /// <param name="baseNotional">What the lane would have traded unscaled.</param>
    /// <param name="targetVolatility">
    /// The volatility this desk wants each position to carry, over the same period as
    /// <paramref name="forecastVolatility"/>. A fraction, so 0.02 is two percent.
    /// </param>
    /// <param name="forecastVolatility">
    /// What the instrument is forecast to do over that period, from the fitted variance model.
    /// </param>
    /// <param name="maximumNotional">The venue or risk cap, which the scale may never exceed.</param>
    /// <remarks>
    /// An unusable forecast returns the base notional rather than a guess. Refusing to size at all
    /// would stop the lane during a warm-up or a feed gap, and inventing a scale from a
    /// non-finite estimate is exactly the silent-confidence failure the model bridge exists to
    /// prevent -- so the honest fallback is the size that was going to be used anyway.
    /// </remarks>
    public static decimal NotionalFor(
        decimal baseNotional,
        double targetVolatility,
        double forecastVolatility,
        decimal maximumNotional)
    {
        if (baseNotional <= 0m) return 0m;
        if (!double.IsFinite(targetVolatility) || targetVolatility <= 0d) return Cap(baseNotional);
        if (!double.IsFinite(forecastVolatility) || forecastVolatility <= 0d) return Cap(baseNotional);

        double scale = Math.Clamp(targetVolatility / forecastVolatility, MinimumScale, MaximumScale);

        return Cap(decimal.Round(baseNotional * (decimal)scale, 2, MidpointRounding.ToZero));

        decimal Cap(decimal notional) =>
            maximumNotional > 0m && notional > maximumNotional ? maximumNotional : notional;
    }

    /// <summary>
    /// Turns a per-bar variance into a volatility over <paramref name="bars"/> of them.
    ///
    /// The variance models speak in mean squared log return per bar, which is the one unit in this
    /// system that has already caused a real defect: GARCH is fitted on percent returns and HAR on
    /// log returns, a factor of ten thousand apart, and left unconverted the two appeared to
    /// disagree by four orders of magnitude on every bar. Converting in one named place, rather
    /// than at each call site, is the lesson from that.
    ///
    /// Scaling by the square root of bar count assumes returns are serially uncorrelated. They are
    /// not exactly -- that is the entire premise of the trend and reversion families -- so this is
    /// an approximation, and a well-understood one. It is stated here rather than implied.
    /// </summary>
    public static double VolatilityOver(double perBarVariance, int bars)
    {
        if (!double.IsFinite(perBarVariance) || perBarVariance <= 0d || bars <= 0) return double.NaN;

        return Math.Sqrt(perBarVariance * bars);
    }
}
