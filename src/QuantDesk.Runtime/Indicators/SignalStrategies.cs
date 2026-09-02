using QuantDesk.Domain.Trading;

namespace QuantDesk.Runtime.Indicators;

/// <summary>How much live evidence a strategy has earned, and therefore what it is allowed to do.</summary>
public enum StrategyQualification
{
    /// <summary>
    /// Tested and did not clear its own trading costs at a lower confidence bound.
    ///
    /// It may still trade in experimental mode, because the point of that mode is to collect live
    /// evidence about things that have not qualified. It may never be described as an edge.
    /// </summary>
    Unqualified,

    /// <summary>Cleared its costs on out-of-sample research and is awaiting live confirmation.</summary>
    ResearchTested,

    /// <summary>Cleared research and live evidence. The only state permitted to trade on its own merit.</summary>
    Qualified,
}

/// <summary>One entry hypothesis: a named rule and the mechanism it claims to exploit.</summary>
/// <param name="Id">Stable identifier, versioned so a changed rule cannot reuse its predecessor's record.</param>
/// <param name="Mechanism">The family it belongs to, used to keep the rotation across mechanisms.</param>
/// <param name="Qualification">What the evidence currently supports.</param>
/// <param name="ResearchMeanNetBps">Mean net return per trade in research, after measured costs.</param>
/// <param name="ResearchLowerBoundBps">Lower 95% bound on that mean. Above zero is what qualifying means.</param>
public sealed record SignalStrategy(
    string Id,
    string Mechanism,
    StrategyQualification Qualification,
    double ResearchMeanNetBps,
    double ResearchLowerBoundBps,
    Func<IndicatorSet, int, bool> Fires);

/// <summary>
/// The strategy set the lane may draw from.
///
/// Why there are several, and why none of them is trusted
/// -----------------------------------------------------
/// The lane traded one rule -- dual-horizon price momentum -- which is a single mechanism reading
/// nothing but closes. Fifteen families across trend, mean reversion, breakout, volatility regime
/// and volume confirmation were tested on five-minute bars over sixty days, across seven crypto
/// pairs and four equity ETFs, on non-overlapping trades, net of the cost each venue actually
/// charges.
///
/// Not one produced a mean net return whose lower confidence bound sat above zero. The measured
/// figures are carried on each entry below, so a strategy cannot be discussed without its evidence.
///
/// The shape of the result is worth recording, because it is more useful than the verdict. On
/// crypto the families lose roughly 50-70 bps against a 68 bps round trip; on equities they lose
/// roughly 5-15 against an 8 bps round trip. In both cases the loss is approximately the cost,
/// which says these rules capture close to zero gross edge and simply pay the toll. Cost is not
/// what makes them unprofitable -- it only sets the rate.
///
/// They are still run, in experimental mode, because live evidence across many mechanisms is worth
/// more than live evidence about one, and because a rule that has never traded has no live record
/// at all. Nothing here may be promoted on a backtest.
/// </summary>
public static class SignalStrategies
{
    /// <summary>
    /// Every strategy, with the research figures that describe it.
    ///
    /// Measured 2026-09-02 on 5-minute bars over ~60 days. The bounds are two-sided 95% intervals
    /// on the mean of non-overlapping trades, net of 68 bps for crypto and 8 bps for equities.
    /// </summary>
    public static IReadOnlyList<SignalStrategy> ForCrypto { get; } =
    [
        Trend("trend.momentum-dual-horizon.v1", -63.3, -70.1, MomentumDualHorizon),
        Trend("trend.ema-cross-12-48.v1", -71.1, -84.9, EmaCross),
        Trend("trend.macd-histogram-flip.v1", -60.0, -68.5, MacdFlip),
        Trend("trend.adx-filtered.v1", -48.2, -68.4, AdxFilteredTrend),
        Reversion("reversion.rsi-oversold.v1", -59.5, -69.9, RsiOversold),
        Reversion("reversion.bollinger-lower.v1", -49.8, -68.9, BollingerLowerTouch),
        Reversion("reversion.stochastic-oversold.v1", -56.1, -71.2, StochasticOversold),
        Reversion("reversion.vwap.v1", -50.5, -69.5, VwapReversion),
        Breakout("breakout.donchian-20.v1", -70.0, -85.0, DonchianBreakout),
        Breakout("breakout.bollinger-upper.v1", -72.0, -88.0, BollingerUpperBreak),
        Volume("volume.surge-breakout.v1", -60.1, -94.2, VolumeSurgeBreakout),
        Volume("volume.obv-confirmed-trend.v1", -62.0, -72.0, ObvConfirmedTrend),
        Volatility("volatility.atr-expansion.v1", -57.5, -71.7, AtrExpansionTrend),
    ];

    /// <summary>
    /// The equity set. Same mechanisms, different measured figures.
    ///
    /// These lose an order of magnitude less than their crypto counterparts, entirely because the
    /// round trip costs an order of magnitude less. The gross edge is no better.
    /// </summary>
    public static IReadOnlyList<SignalStrategy> ForEquity { get; } =
    [
        Reversion("reversion.vwap.v1", 3.3, -13.6, VwapReversion),
        Reversion("reversion.rsi-oversold.v1", -2.4, -12.4, RsiOversold),
        Reversion("reversion.bollinger-lower.v1", -5.3, -11.1, BollingerLowerTouch),
        Trend("trend.macd-histogram-flip.v1", -5.5, -10.5, MacdFlip),
        Trend("trend.momentum-dual-horizon.v1", -8.3, -11.8, MomentumDualHorizon),
        Reversion("reversion.stochastic-oversold.v1", -8.1, -12.3, StochasticOversold),
        Volume("volume.obv-confirmed-trend.v1", -8.7, -12.4, ObvConfirmedTrend),
        Trend("trend.adx-filtered.v1", -9.5, -15.0, AdxFilteredTrend),
        Breakout("breakout.donchian-20.v1", -9.8, -14.5, DonchianBreakout),
        Volatility("volatility.atr-expansion.v1", -9.5, -14.0, AtrExpansionTrend),
        Breakout("breakout.bollinger-upper.v1", -11.4, -17.4, BollingerUpperBreak),
        Volume("volume.surge-breakout.v1", -3.6, -17.5, VolumeSurgeBreakout),
        Trend("trend.ema-cross-12-48.v1", -15.6, -23.6, EmaCross),
    ];

    /// <summary>
    /// Whether this strategy is known to lose, as opposed to merely unproven.
    ///
    /// The distinction the lane needs and did not have. Every strategy here is Unqualified, which
    /// says only that none has *demonstrated* an edge. It says nothing about how badly each one is
    /// measured to do, and those are very different situations: a family whose mean net return is
    /// indistinguishable from breakeven is worth paying a little to learn about, and one measured
    /// at minus sixty basis points against a sixty-eight basis point round trip is not -- it is a
    /// known, repeatable loss with no information left to buy.
    ///
    /// The test is whether the mean sits more than one standard error below zero. Inside that, the
    /// family might be flat and the sample too small to tell. Beyond it, the loss is the finding.
    ///
    /// Live confirmation, 2026-09-02: nine crypto round trips, 1,799 USD of notional, 9.00 USD of
    /// round-trip fees, and an account down 6.57 USD. Gross price movement was approximately
    /// nothing and the fee was the entire loss -- exactly what the research said would happen.
    /// </summary>
    public static bool IsKnownToLose(this SignalStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);

        // The published bound is two-sided at 95%, so the standard error is the gap over 1.96.
        double standardError = Math.Max(
            (strategy.ResearchMeanNetBps - strategy.ResearchLowerBoundBps) / 1.96, 0.0);

        return strategy.Qualification != StrategyQualification.Qualified
            && strategy.ResearchMeanNetBps < -standardError;
    }

    /// <summary>
    /// The strategies a lane may actually open a position with.
    ///
    /// Everything measured to lose is removed. On crypto that is all of them, so the crypto lane
    /// stops opening positions -- which is the correct reading of the evidence rather than a
    /// failure of the lane. On equities, where a round trip costs about eight basis points instead
    /// of sixty-eight, several families sit close enough to breakeven to remain worth observing.
    /// </summary>
    public static IReadOnlyList<SignalStrategy> Tradable(TradedAssetClass assetClass) =>
        [.. For(assetClass).Where(strategy => !strategy.IsKnownToLose())];

    /// <summary>
    /// The strategies that need nothing but closing prices.
    ///
    /// Used when the feed returns closes without highs, lows and volumes, or returns too little
    /// history for the slower indicators to have warmed up. The lane falls back to these rather
    /// than falling silent: a feed hiccup that briefly shortens the series should narrow what the
    /// lane can consider, not stop it considering anything.
    /// </summary>
    public static IReadOnlyList<SignalStrategy> ClosesOnly(TradedAssetClass assetClass) =>
        [.. For(assetClass).Where(strategy =>
            strategy.Id is "trend.momentum-dual-horizon.v1" or "trend.ema-cross-12-48.v1"
                or "trend.macd-histogram-flip.v1")];

    public static IReadOnlyList<SignalStrategy> For(TradedAssetClass assetClass) =>
        assetClass is TradedAssetClass.SpotCrypto ? ForCrypto : ForEquity;

    // ------------------------------------------------------------------ rules
    // Each reads the bar at `i` and the one before it. A rule that needs a value the set has not
    // warmed up returns false rather than treating a NaN comparison as a decision.

    /// <summary>
    /// The rule the lane traded before any of this existed, kept as the control.
    ///
    /// The spans match the cost gate's exactly -- twelve intervals and three, which is what
    /// comparing the thirteenth-from-last and fourth-from-last closes to the latest one means. They
    /// have to agree: the gate admits an opportunity on that comparison, and a strategy claiming to
    /// be the same rule while measuring a different span would sometimes fire where the gate had
    /// just refused, and sometimes refuse where the gate had just admitted.
    /// </summary>
    private static bool MomentumDualHorizon(IndicatorSet s, int i)
    {
        if (i < 12) return false;
        double medium = ((s.Close[i] / s.Close[i - 12]) - 1) * 10_000;
        double shortRun = ((s.Close[i] / s.Close[i - 3]) - 1) * 10_000;
        return medium > 0 && shortRun > 0;
    }

    private static bool EmaCross(IndicatorSet s, int i) =>
        i > 0 && s.IsReadyAt(i, s.Ema12, s.Ema48) && s.IsReadyAt(i - 1, s.Ema12, s.Ema48)
        && s.Ema12[i] > s.Ema48[i] && s.Ema12[i - 1] <= s.Ema48[i - 1];

    private static bool MacdFlip(IndicatorSet s, int i) =>
        i > 0 && s.IsReadyAt(i, s.MacdHistogram) && s.IsReadyAt(i - 1, s.MacdHistogram)
        && s.MacdHistogram[i] > 0 && s.MacdHistogram[i - 1] <= 0;

    private static bool AdxFilteredTrend(IndicatorSet s, int i) =>
        s.IsReadyAt(i, s.Adx14, s.PlusDi, s.MinusDi, s.Ema48)
        && s.Adx14[i] > 25 && s.PlusDi[i] > s.MinusDi[i] && s.Close[i] > s.Ema48[i];

    private static bool RsiOversold(IndicatorSet s, int i) =>
        i > 0 && s.IsReadyAt(i, s.Rsi14) && s.IsReadyAt(i - 1, s.Rsi14)
        && s.Rsi14[i] > 30 && s.Rsi14[i - 1] <= 30;

    private static bool BollingerLowerTouch(IndicatorSet s, int i) =>
        i > 0 && s.IsReadyAt(i, s.BollingerLower) && s.IsReadyAt(i - 1, s.BollingerLower)
        && s.Close[i] > s.BollingerLower[i] && s.Close[i - 1] <= s.BollingerLower[i - 1];

    private static bool StochasticOversold(IndicatorSet s, int i) =>
        i > 0 && s.IsReadyAt(i, s.StochasticK, s.StochasticD)
        && s.IsReadyAt(i - 1, s.StochasticK, s.StochasticD)
        && s.StochasticK[i] > s.StochasticD[i] && s.StochasticK[i - 1] <= s.StochasticD[i - 1]
        && s.StochasticK[i] < 30;

    private static bool VwapReversion(IndicatorSet s, int i)
    {
        if (!s.IsReadyAt(i, s.Vwap48, s.Atr14)) return false;
        double gap = s.Close[i] - s.Vwap48[i];
        // Measured in ATRs rather than raw price, so the same rule means the same thing on a
        // 60,000-dollar instrument and a 12-dollar one.
        return gap < 0 && s.Atr14[i] > 0 && Math.Abs(gap) / s.Atr14[i] > 1.5;
    }

    private static bool DonchianBreakout(IndicatorSet s, int i) =>
        i > 0 && s.IsReadyAt(i, s.DonchianHigh) && s.IsReadyAt(i - 1, s.DonchianHigh)
        && s.Close[i] > s.DonchianHigh[i] && s.Close[i - 1] <= s.DonchianHigh[i - 1];

    private static bool BollingerUpperBreak(IndicatorSet s, int i) =>
        i > 0 && s.IsReadyAt(i, s.BollingerUpper) && s.IsReadyAt(i - 1, s.BollingerUpper)
        && s.Close[i] > s.BollingerUpper[i] && s.Close[i - 1] <= s.BollingerUpper[i - 1];

    private static bool VolumeSurgeBreakout(IndicatorSet s, int i) =>
        s.IsReadyAt(i, s.DonchianHigh, s.VolumeZ48)
        && s.Close[i] > s.DonchianHigh[i] && s.VolumeZ48[i] > 2.0;

    private static bool ObvConfirmedTrend(IndicatorSet s, int i) =>
        s.IsReadyAt(i, s.ObvSlope12, s.Ema48, s.Rsi14)
        && s.ObvSlope12[i] > 0 && s.Close[i] > s.Ema48[i] && s.Rsi14[i] > 50;

    private static bool AtrExpansionTrend(IndicatorSet s, int i)
    {
        if (i < 14 || !s.IsReadyAt(i, s.Atr14, s.Ema48) || !s.IsReadyAt(i - 1, s.Atr14)) return false;
        double medium = ((s.Close[i] / s.Close[i - 12]) - 1) * 10_000;
        return s.Atr14[i] > s.Atr14[i - 1] && s.Close[i] > s.Ema48[i] && medium > 0;
    }

    private static SignalStrategy Trend(string id, double mean, double lower, Func<IndicatorSet, int, bool> f) =>
        new(id, "trend", StrategyQualification.Unqualified, mean, lower, f);

    private static SignalStrategy Reversion(string id, double mean, double lower, Func<IndicatorSet, int, bool> f) =>
        new(id, "reversion", StrategyQualification.Unqualified, mean, lower, f);

    private static SignalStrategy Breakout(string id, double mean, double lower, Func<IndicatorSet, int, bool> f) =>
        new(id, "breakout", StrategyQualification.Unqualified, mean, lower, f);

    private static SignalStrategy Volume(string id, double mean, double lower, Func<IndicatorSet, int, bool> f) =>
        new(id, "volume", StrategyQualification.Unqualified, mean, lower, f);

    private static SignalStrategy Volatility(string id, double mean, double lower, Func<IndicatorSet, int, bool> f) =>
        new(id, "volatility", StrategyQualification.Unqualified, mean, lower, f);
}
