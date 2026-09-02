using QuantDesk.Domain.Trading;

using QuantDesk.Runtime.Research;

namespace QuantDesk.Runtime.Indicators;

/// <summary>How much live evidence a strategy has earned, and therefore what it is allowed to do.</summary>
/// <summary>
/// Round-trip costs the 2026-09-02 out-of-sample scan charged, in basis points.
///
/// Crypto is the figure the broker-side reconstruction measured across 75 live round trips; equity
/// is one basis point of fee, two of slippage and a fifth of a point of spread, rounded up. They
/// live here because the net figures in this file are only interpretable against them.
/// </summary>
public static class ResearchCostAssumptions
{
    public const double Crypto = 33.7;
    public const double Equity = 8.0;
}

/// <summary>
/// What the venue actually charges for a round trip, in basis points, excluding live spread.
///
/// Verified against delivered quantities rather than taken from a document. Alpaca's spot crypto
/// fee is charged in kind, so the entry-side rate is directly observable as the shortfall between
/// what an order bought and what the account could then sell. Measured across 62 matched round
/// trips on 2026-09-02: a median of 25.0 bps retained per entry, which is exactly the published
/// taker rate, and 50 bps for the round trip -- plus the separate USD charge on top.
///
/// This matters because it is not what the research scan charged. The scan used 33.7 bps, a figure
/// taken from an earlier reconstruction that understated the fee, so every crypto net figure in the
/// registry is roughly 26 bps too generous. Judging a rule by that net figure asks whether it beat
/// a cost the account does not pay.
/// </summary>
public static class VenueRoundTripCosts
{
    /// <summary>25 bps taker per side, charged in kind, plus the slippage the lane budgets.</summary>
    public const double Crypto = 60.0;

    /// <summary>Commission-free, with pass-through fees and budgeted slippage.</summary>
    public const double Equity = 8.0;

    public static double For(TradedAssetClass assetClass) =>
        assetClass is TradedAssetClass.SpotCrypto ? Crypto : Equity;
}

public enum StrategyQualification
{
    /// <summary>
    /// Tested and did not clear its own trading costs at a lower confidence bound.
    ///
    /// It may still trade in experimental mode, because the point of that mode is to collect live
    /// evidence about things that have not qualified. It may never be described as an edge.
    /// </summary>
    Unqualified,

    /// <summary>
    /// The rule changed, or could not be re-measured, so its figures describe something else.
    ///
    /// Distinct from Unqualified in the way that matters for trading. Unqualified means measured and
    /// found wanting -- a real number, near enough to breakeven that buying more evidence is
    /// reasonable. Stale means the number on the record was produced by a different rule or a
    /// different cost, so it is not evidence about this rule at all.
    ///
    /// The distinction had teeth immediately. After the 2026-09-02 re-measurement two rules could
    /// not be re-evaluated -- donchian-breakout-20 and volume-surge-breakout produced fewer than
    /// twelve non-overlapping trades in the held-out half -- and volume-surge-breakout's stale
    /// equity figure of -3.6 sat close enough to zero to pass the known-to-lose test. It would have
    /// been the only equity rule still trading, on the strength of a number measured against a
    /// volume z-score the code no longer computes.
    /// </summary>
    Stale,

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
    Func<IndicatorSet, int, bool> Fires)
{
    /// <summary>
    /// Indicator series this rule cannot decide without.
    ///
    /// Declared so that "nothing fired" can be told apart from "the feature this rule needs could
    /// not be computed". Both look identical from outside -- an unavailable series is all NaN, and
    /// every rule reading NaN declines -- but one is a market observation and the other is a
    /// degraded system, and only the second is worth telling anyone about.
    ///
    /// Only the series whose absence is decisive are listed. A rule that reads Close alone declares
    /// nothing, because Close is the one series whose presence makes a set exist at all.
    /// </summary>
    public IReadOnlyList<string> RequiredSeries { get; init; } = [];

    /// <summary>
    /// The round-trip cost the research figures were measured against, in basis points.
    ///
    /// Recorded so that a net figure can be turned back into a gross one without a magic number.
    /// ResearchMeanNetBps is net of whatever the scan charged, and a candidate's gross expected
    /// edge has to be gross -- the cost is subtracted again downstream, and subtracting it twice
    /// would understate every edge by a full round trip.
    /// </summary>
    public double ResearchCostAssumptionBps { get; init; }

    /// <summary>
    /// What this rule is measured to earn before costs.
    ///
    /// The number a candidate's expected edge should carry. What the lane used instead was the
    /// instrument's expected move over the holding period -- an ATR magnitude scaled by the square
    /// root of time, which says how far the instrument typically travels and nothing about which
    /// way. On 2026-09-02 that put 170 bps on a candidate whose rule is measured at 1.5 bps net,
    /// about 35 gross: a hundredfold overstatement that both rubber-stamped the risk governor's
    /// net-edge gate and set a profit target the position could never reach.
    /// </summary>
    public double ResearchMeanGrossBps => ResearchMeanNetBps + ResearchCostAssumptionBps;
}

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
    /// <summary>
    /// Out-of-sample means and 95% lower bounds, from the 2026-09-02 re-run.
    ///
    /// What changed, and why every number moved
    /// ----------------------------------------
    /// The first scan ranked and reported on one undivided sixty-day block at an assumed 68 bps
    /// round trip, with no correction for having examined ninety combinations. This run selects on
    /// a chronological training half and reports on the held-out half, charges the 33.7 bps the
    /// broker-side reconstruction actually measured, and uses the corrected indicator definitions.
    /// Figures are taken at the holding period each lane actually uses: four hours for crypto, two
    /// for equities.
    ///
    /// Across 39 crypto trials PBO is 0.230 and the deflated Sharpe ratio is 0.000; across 39
    /// equity trials PBO is 0.258 and the deflated Sharpe is 0.000. Nothing survives its own trial
    /// count, and no family is positive in both halves at the lower bound. That is the finding.
    ///
    /// The single most consequential correction is reversion.vwap.v1, which was the best-scoring
    /// equity rule in the book at +3.3 bps. Measured against a session-scoped VWAP -- the
    /// definition VWAP actually has -- it is -7.9 out of sample. The rule that looked promising was
    /// an artefact of a rolling window wearing VWAP's name.
    ///
    /// Two rules could not be evaluated: donchian-breakout-20 and volume-surge-breakout produced
    /// fewer than twelve non-overlapping trades in one of the halves. They keep their stale figures
    /// and must not be described as measured.
    ///
    /// Superseded note, kept for provenance: these numbers previously predated the indicator
    /// corrections.
    ///
    /// Four rules now read a different series than the one their figures were measured on: VWAP is
    /// session-scoped for equities rather than a 48-bar rolling window, the volume anomaly is scored
    /// against the same time of day on previous days rather than against its own trailing window,
    /// and the two momentum rules measure an hour and a quarter-hour of time rather than twelve and
    /// three bars. reversion.vwap.v1, volume.surge-breakout.v1, trend.momentum-dual-horizon.v1 and
    /// volatility.atr-expansion.v1 are therefore rules whose research no longer describes them.
    ///
    /// The numbers are kept rather than blanked because they still bound the families conservatively
    /// -- every crypto figure is negative by more than its own standard error, and correcting an
    /// ill-posed feature is not a reason to expect that to reverse. But they are stale, and no rule
    /// among those four should be described as measured until the scan is re-run against the
    /// corrected definitions and the corrected 33.7 bps round trip.
    /// </summary>
    public static IReadOnlyList<SignalStrategy> ForCrypto { get; } =
    [
        Trend("trend.momentum-dual-horizon.v1", -11.4, -23.4, MomentumDualHorizon),
        Trend("trend.ema-cross-12-48.v1", -13.0, -27.6, EmaCross),
        Trend("trend.macd-histogram-flip.v1", -15.3, -27.4, MacdFlip),
        Trend("trend.adx-filtered.v1", -9.9, -28.5, AdxFilteredTrend),
        Reversion("reversion.rsi-oversold.v1", -15.9, -33.5, RsiOversold),
        Reversion("reversion.bollinger-lower.v1", -7.8, -21.2, BollingerLowerTouch),
        Reversion("reversion.stochastic-oversold.v1", -18.4, -30.9, StochasticOversold),
        Reversion("reversion.vwap.v1", -10.2, -23.1, VwapReversion) with { RequiredSeries = ["Vwap48"] },
        // Stale: too few non-overlapping trades in the held-out half to re-measure.
        Breakout("breakout.donchian-20.v1", -70.0, -85.0, DonchianBreakout) with { Qualification = StrategyQualification.Stale },
        Breakout("breakout.bollinger-upper.v1", 1.5, -14.5, BollingerUpperBreak),
        // Stale: too few non-overlapping trades in the held-out half to re-measure.
        Volume("volume.surge-breakout.v1", -60.1, -94.2, VolumeSurgeBreakout)
            with { Qualification = StrategyQualification.Stale, RequiredSeries = ["VolumeZ48"] },
        Volume("volume.obv-confirmed-trend.v1", -9.6, -24.4, ObvConfirmedTrend) with { RequiredSeries = ["ObvSlope12"] },
        Volatility("volatility.atr-expansion.v1", -13.8, -27.0, AtrExpansionTrend),
    ];

    /// <summary>
    /// The equity set. Same mechanisms, different measured figures.
    ///
    /// These lose an order of magnitude less than their crypto counterparts, entirely because the
    /// round trip costs an order of magnitude less. The gross edge is no better.
    /// </summary>
    public static IReadOnlyList<SignalStrategy> ForEquity { get; } =
    [
        Reversion("reversion.vwap.v1", -7.9, -10.6, VwapReversion, ResearchCostAssumptions.Equity) with { RequiredSeries = ["Vwap48"] },
        Reversion("reversion.rsi-oversold.v1", -5.7, -11.0, RsiOversold, ResearchCostAssumptions.Equity),
        Reversion("reversion.bollinger-lower.v1", -6.5, -9.7, BollingerLowerTouch, ResearchCostAssumptions.Equity),
        Trend("trend.macd-histogram-flip.v1", -9.6, -12.4, MacdFlip, ResearchCostAssumptions.Equity),
        Trend("trend.momentum-dual-horizon.v1", -11.2, -13.5, MomentumDualHorizon, ResearchCostAssumptions.Equity),
        Reversion("reversion.stochastic-oversold.v1", -9.3, -12.0, StochasticOversold, ResearchCostAssumptions.Equity),
        Volume("volume.obv-confirmed-trend.v1", -12.7, -15.7, ObvConfirmedTrend, ResearchCostAssumptions.Equity) with { RequiredSeries = ["ObvSlope12"] },
        Trend("trend.adx-filtered.v1", -10.4, -14.7, AdxFilteredTrend, ResearchCostAssumptions.Equity),
        // Stale: too few non-overlapping trades in the held-out half to re-measure.
        Breakout("breakout.donchian-20.v1", -9.8, -14.5, DonchianBreakout, ResearchCostAssumptions.Equity) with { Qualification = StrategyQualification.Stale },
        Volatility("volatility.atr-expansion.v1", -13.0, -16.1, AtrExpansionTrend, ResearchCostAssumptions.Equity),
        Breakout("breakout.bollinger-upper.v1", -13.2, -17.0, BollingerUpperBreak, ResearchCostAssumptions.Equity),
        // Stale: too few non-overlapping trades in the held-out half to re-measure.
        Volume("volume.surge-breakout.v1", -3.6, -17.5, VolumeSurgeBreakout, ResearchCostAssumptions.Equity)
            with { Qualification = StrategyQualification.Stale, RequiredSeries = ["VolumeZ48"] },
        Trend("trend.ema-cross-12-48.v1", -11.8, -16.8, EmaCross, ResearchCostAssumptions.Equity),
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
    public static bool IsKnownToLose(this SignalStrategy strategy) =>
        strategy.IsKnownToLose(strategy.ResearchCostAssumptionBps);

    /// <inheritdoc cref="IsKnownToLose(SignalStrategy)"/>
    /// <param name="strategy">The rule under test.</param>
    /// <param name="venueRoundTripBps">
    /// The round trip the account actually pays, which is not always what the scan charged.
    /// </param>
    public static bool IsKnownToLose(this SignalStrategy strategy, double venueRoundTripBps)
    {
        ArgumentNullException.ThrowIfNull(strategy);

        // Judged against the cost the account pays, not the cost the scan assumed.
        //
        // The 2026-09-02 scan charged 33.7 bps for a crypto round trip. Measured against delivered
        // quantities the same day, the venue keeps 25.0 bps in kind on the entry alone -- 50 for
        // the round trip, plus slippage and the separate USD charge. So every crypto net figure in
        // the registry is about 26 bps too generous, and asking whether a rule beat 33.7 asks
        // whether it beat a cost that does not exist.
        //
        // breakout.bollinger-upper.v1 is the case in point. It measured +1.5 bps net against 33.7
        // and was the one rule the crypto lane still traded. Against the 60 the account is charged
        // it is roughly -25, which is well outside its own error bar.
        double gross = strategy.ResearchMeanNetBps + strategy.ResearchCostAssumptionBps;
        double net = gross - venueRoundTripBps;

        // The published bound is two-sided at 95%, so the standard error is the gap over 1.96. The
        // spread of outcomes does not change when the cost assumption does; only the mean moves.
        double standardError = Math.Max(
            (strategy.ResearchMeanNetBps - strategy.ResearchLowerBoundBps) / 1.96, 0.0);

        return strategy.Qualification != StrategyQualification.Qualified && net < -standardError;
    }

    /// <summary>
    /// The strategies a lane may actually open a position with.
    ///
    /// Everything measured to lose is removed. On crypto that is all of them, so the crypto lane
    /// stops opening positions -- which is the correct reading of the evidence rather than a
    /// failure of the lane. On equities, where a round trip costs about eight basis points instead
    /// of sixty-eight, several families sit close enough to breakeven to remain worth observing.
    /// </summary>
    /// <summary>
    /// The rules an exploration budget may buy information about, least-bad first.
    ///
    /// What this is not
    /// ----------------
    /// It is not a relaxation of IsKnownToLose. That test still says exactly what it said before,
    /// and every rule here still fails it: at the sixty basis points the venue charges, nothing in
    /// either book has a positive expected edge. The 2026-09-02 AVAX round trip put a number on
    /// what that costs -- the price moved 46.6 bps in the rule's favour and the account still lost
    /// 69 cents, because the round trip cost 81.2.
    ///
    /// What it is
    /// ----------
    /// A deliberate decision to pay for evidence. Section 12.2 calls for an exploration bonus for
    /// under-tested but eligible rules, and section 20.4 gives it a rung of its own between shadow
    /// and exploitation. A desk that only ever trades what it has already proven can never learn
    /// anything new; one that trades everything learns expensively. The compromise is a bounded
    /// budget, spent on the candidates that are closest to viable, with the expected loss stated in
    /// advance rather than discovered afterwards.
    ///
    /// Ordered by measured gross edge, so the budget goes to the rules with the most to prove
    /// rather than to whichever fires first. Stale rules are excluded: their figures describe a
    /// different rule, so there is no sense in which they are "closest to viable".
    /// </summary>
    public static IReadOnlyList<SignalStrategy> Explorable(TradedAssetClass assetClass) =>
    [
        .. For(assetClass)
            .Where(strategy => strategy.Qualification is not StrategyQualification.Stale)
            .OrderByDescending(strategy => strategy.ResearchMeanGrossBps)
            .ThenBy(strategy => strategy.Id, StringComparer.Ordinal),
    ];

    /// <summary>
    /// What an exploration position is expected to cost, in basis points, as a positive number.
    ///
    /// Stated so that nothing downstream has to infer it. This is the price of the evidence, and a
    /// budget that does not know its own price is not a budget.
    /// </summary>
    public static double ExpectedExplorationCostBps(
        this SignalStrategy strategy, double venueRoundTripBps)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        return Math.Max(venueRoundTripBps - strategy.ResearchMeanGrossBps, 0d);
    }

    public static IReadOnlyList<SignalStrategy> Tradable(TradedAssetClass assetClass) =>
        Tradable(assetClass, shadow: null);

    /// <summary>
    /// Minimum resolved shadow signals before live evidence may overrule a backtest.
    ///
    /// Thirty. Enough that a mean and its interval describe the rule rather than the fortnight, and
    /// few enough that a rule firing a handful of times a day can clear it inside a month.
    /// </summary>
    public const int MinimumShadowSignals = 30;

    /// <inheritdoc cref="Tradable(TradedAssetClass)"/>
    /// <param name="assetClass">The lane's instrument class.</param>
    /// <param name="shadow">
    /// What each rule has earned in shadow, keyed by strategy id, or null when there is none.
    /// </param>
    /// <remarks>
    /// Live evidence overrules the backtest, in both directions.
    ///
    /// Promotion, because a rule stood down on a backtest can otherwise never come back: it does
    /// not trade, so it produces no evidence, so nothing can requalify it. A shadow record whose
    /// 95% lower bound clears zero after the venue's round trip is better evidence than the scan
    /// that stood the rule down, for the reason section 20.1 cares about -- it was collected after
    /// the decision to collect it, so it cannot have been fitted.
    ///
    /// Demotion, because the same argument runs the other way. A rule the research likes whose live
    /// record is measurably negative is not a rule to keep trading while the backtest catches up.
    ///
    /// Shadow figures are an upper bound: they pay the venue's fee but never crossed the book, so
    /// no spread and no slippage. Promotion therefore requires the lower bound to clear zero rather
    /// than the mean, which is the same conservatism the registry's own figures are read with.
    /// </remarks>
    public static IReadOnlyList<SignalStrategy> Tradable(
        TradedAssetClass assetClass,
        IReadOnlyDictionary<string, ShadowSummary>? shadow)
    {
        double venueCost = VenueRoundTripCosts.For(assetClass);
        List<SignalStrategy> tradable = [];

        foreach (SignalStrategy strategy in For(assetClass))
        {
            // A rule whose research describes a rule the code no longer computes is not evidence
            // either way, and shadow cannot rescue it -- it would be promoting on one measurement
            // while the recorded one describes something else.
            if (strategy.Qualification is StrategyQualification.Stale) continue;

            bool researchSaysNo = strategy.IsKnownToLose(venueCost);

            if (shadow is not null &&
                shadow.TryGetValue(strategy.Id, out ShadowSummary live) &&
                live.Signals >= MinimumShadowSignals)
            {
                // Live evidence decides, whichever way it points.
                if (live.LowerBoundBps > 0d) tradable.Add(strategy);
                continue;
            }

            if (!researchSaysNo) tradable.Add(strategy);
        }

        return tradable;
    }

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
        // An hour and a quarter of an hour, measured in time rather than in bars.
        //
        // Twelve five-minute bars equal an hour only while the feed returns an unbroken sequence.
        // Across a halt, a dropped bar, or an equity session boundary the two diverge, and the rule
        // keeps computing a number that now describes a different span than the one it was
        // calibrated on. The bar counts remain the fallback for a series with no time axis, which
        // is exactly the behaviour this had before.
        int medium = s.IndexAtOrBefore(i, MediumHorizon, fallbackBars: 12);
        int shortRun = s.IndexAtOrBefore(i, ShortHorizon, fallbackBars: 3);
        if (medium < 0 || shortRun < 0) return false;

        return s.Close[i] > s.Close[medium] && s.Close[i] > s.Close[shortRun];
    }

    /// <summary>The spans the dual-horizon rule and the cost gate both measure over.</summary>
    private static readonly TimeSpan MediumHorizon = TimeSpan.FromMinutes(60);

    /// <inheritdoc cref="MediumHorizon"/>
    private static readonly TimeSpan ShortHorizon = TimeSpan.FromMinutes(15);

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
        int mediumIndex = s.IndexAtOrBefore(i, MediumHorizon, fallbackBars: 12);
        if (mediumIndex < 0) return false;
        double medium = ((s.Close[i] / s.Close[mediumIndex]) - 1) * 10_000;
        return s.Atr14[i] > s.Atr14[i - 1] && s.Close[i] > s.Ema48[i] && medium > 0;
    }

    private static SignalStrategy Trend(
        string id, double mean, double lower, Func<IndicatorSet, int, bool> f,
        double costAssumptionBps = ResearchCostAssumptions.Crypto) =>
        new(id, "trend", StrategyQualification.Unqualified, mean, lower, f)
        { ResearchCostAssumptionBps = costAssumptionBps };

    private static SignalStrategy Reversion(
        string id, double mean, double lower, Func<IndicatorSet, int, bool> f,
        double costAssumptionBps = ResearchCostAssumptions.Crypto) =>
        new(id, "reversion", StrategyQualification.Unqualified, mean, lower, f)
        { ResearchCostAssumptionBps = costAssumptionBps };

    private static SignalStrategy Breakout(
        string id, double mean, double lower, Func<IndicatorSet, int, bool> f,
        double costAssumptionBps = ResearchCostAssumptions.Crypto) =>
        new(id, "breakout", StrategyQualification.Unqualified, mean, lower, f)
        { ResearchCostAssumptionBps = costAssumptionBps };

    private static SignalStrategy Volume(
        string id, double mean, double lower, Func<IndicatorSet, int, bool> f,
        double costAssumptionBps = ResearchCostAssumptions.Crypto) =>
        new(id, "volume", StrategyQualification.Unqualified, mean, lower, f)
        { ResearchCostAssumptionBps = costAssumptionBps };

    private static SignalStrategy Volatility(
        string id, double mean, double lower, Func<IndicatorSet, int, bool> f,
        double costAssumptionBps = ResearchCostAssumptions.Crypto) =>
        new(id, "volatility", StrategyQualification.Unqualified, mean, lower, f)
        { ResearchCostAssumptionBps = costAssumptionBps };
}
