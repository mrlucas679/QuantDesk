using QuantDesk.Domain.Trading;
using QuantDesk.Runtime.Indicators;

namespace QuantDesk.Runtime.Tests.Indicators;

/// <summary>
/// The indicators strategies read. These are checked against their published definitions rather
/// than against a library, because the formula is the thing being tested and a library that seeds
/// or smooths differently would make research and execution disagree about what the same indicator
/// means -- a disagreement that surfaces as a strategy which backtested well and traded otherwise.
/// </summary>
public sealed class IndicatorSetTests
{
    [Fact]
    public void ASeriesTooShortToWarmUpProducesNothing()
    {
        // Not a partly-filled set. A caller handed something that looks complete will assume it is,
        // and a 48-period average seeded on 30 bars returns a number that looks valid and is wrong.
        Assert.Null(Build(30));
    }

    [Fact]
    public void MisalignedSeriesAreRefused()
    {
        // Every windowed indicator reads highs, lows and closes by index. A short high series would
        // silently pair each close with the wrong bar's extreme.
        IReadOnlyList<decimal> closes = [.. Enumerable.Range(0, 200).Select(i => 100m + i)];
        Assert.Null(IndicatorSet.Build(closes, closes.Take(150).ToArray(), closes, closes));
    }

    [Fact]
    public void RsiSaturatesOnAnUnbrokenAdvanceRatherThanDividingByZero()
    {
        // A window with no down closes has no average loss. The right answer is 100, not a crash
        // and not a NaN that a comparison would silently read as false.
        IndicatorSet set = Build(200, i => 100m + i)!;

        Assert.Equal(100d, set.Rsi14[^1], precision: 6);
    }

    [Fact]
    public void RsiSitsMidRangeOnAnAlternatingSeries()
    {
        IndicatorSet set = Build(200, i => 100m + (i % 2))!;

        Assert.InRange(set.Rsi14[^1], 40d, 60d);
    }

    [Fact]
    public void AtrUsesWilderSmoothingNotAnExponentialAverage()
    {
        // The distinction that quietly breaks thresholds. Wilder decays at 1/n; an EMA at 2/(n+1).
        // On a constant true range both converge to that range, so the test pins the value rather
        // than the decay -- a series whose range is exactly 2 must give an ATR of exactly 2.
        IReadOnlyList<decimal> closes = [.. Enumerable.Repeat(100m, 200)];
        IReadOnlyList<decimal> highs = [.. Enumerable.Repeat(101m, 200)];
        IReadOnlyList<decimal> lows = [.. Enumerable.Repeat(99m, 200)];
        IndicatorSet set = IndicatorSet.Build(closes, highs, lows, closes)!;

        Assert.Equal(2d, set.Atr14[^1], precision: 6);
    }

    [Fact]
    public void TheDonchianChannelExcludesTheCurrentBar()
    {
        // Causality. Including the current bar's own high would compare it against a window that
        // contains it, so the channel could never be broken and no breakout rule would ever fire.
        // The series advances by 3 while highs sit 1 above the close, so a channel that excluded
        // the current bar is cleared and one that included it could not be.
        IndicatorSet set = Build(200, i => 100m + (3m * i))!;

        Assert.True(set.Close[^1] > set.DonchianHigh[^1]);
    }

    [Fact]
    public void StochasticReadsNeutralOnAFlatWindowRatherThanZero()
    {
        // A flat window has no position within its range. Zero would read as maximally oversold and
        // trigger every reversion rule on the quietest possible market.
        IReadOnlyList<decimal> flat = [.. Enumerable.Repeat(100m, 200)];
        IndicatorSet set = IndicatorSet.Build(flat, flat, flat, flat)!;

        Assert.Equal(50d, set.StochasticK[^1], precision: 6);
    }

    [Fact]
    public void VwapDeclinesRatherThanFallingBackToAnUnweightedMean()
    {
        // No traded volume means no volume-weighted price. Returning the simple mean would answer a
        // different question while looking like an answer to this one.
        IReadOnlyList<decimal> closes = [.. Enumerable.Range(0, 200).Select(i => 100m + i)];
        IReadOnlyList<decimal> zero = [.. Enumerable.Repeat(0m, 200)];
        IndicatorSet set = IndicatorSet.Build(closes, closes, closes, zero)!;

        Assert.False(double.IsFinite(set.Vwap48[^1]));
    }

    [Fact]
    public void EverySeriesIsNanUntilItHasEnoughObservations()
    {
        IndicatorSet set = Build(200)!;

        Assert.False(double.IsFinite(set.Ema48[10]));
        Assert.False(double.IsFinite(set.Rsi14[2]));
        Assert.True(double.IsFinite(set.Ema48[^1]));
    }

    private static IndicatorSet? Build(int n, Func<int, decimal>? price = null)
    {
        price ??= i => 100m + (decimal)Math.Sin(i / 7.0) * 5m;
        IReadOnlyList<decimal> closes = [.. Enumerable.Range(0, n).Select(price)];
        IReadOnlyList<decimal> highs = [.. closes.Select(c => c + 1m)];
        IReadOnlyList<decimal> lows = [.. closes.Select(c => c - 1m)];
        IReadOnlyList<decimal> volumes = [.. Enumerable.Range(0, n).Select(i => 1_000m + i)];
        return IndicatorSet.Build(closes, highs, lows, volumes);
    }
}

/// <summary>
/// Which strategy gets credited with a trade.
///
/// Balanced rather than greedy, because none of these has qualified: choosing by backtest is how a
/// search of thirteen families becomes one overfitted pick, and the families fire at wildly
/// different rates, so letting the most frequent take every trade would produce a live record that
/// is almost entirely one strategy and near-silence about the rest.
/// </summary>
public sealed class StrategyRotationTests
{
    [Fact]
    public void TheStrategyWithTheFewestLiveTradesIsChosen()
    {
        var rotation = new StrategyRotation();
        SignalStrategy busy = Always("a.busy.v1", "trend");
        SignalStrategy quiet = Always("b.quiet.v1", "reversion");
        rotation.RecordTrade(busy);
        rotation.RecordTrade(busy);

        StrategySelection? selection = rotation.Select([busy, quiet], Set()).Selection;

        Assert.Equal("b.quiet.v1", selection!.Strategy.Id);
    }

    [Fact]
    public void TiesBreakTowardTheLeastTradedMechanism()
    {
        // Otherwise a set holding four trend rules and one volume rule quietly becomes a
        // trend-only sample, and the live evidence says nothing about the other mechanisms.
        var rotation = new StrategyRotation();
        SignalStrategy trend = Always("a.trend.v1", "trend");
        SignalStrategy volume = Always("b.volume.v1", "volume");
        rotation.RecordTrade(Always("other.trend.v1", "trend"));

        Assert.Equal("b.volume.v1", rotation.Select([trend, volume], Set()).Selection!.Strategy.Id);
    }

    [Fact]
    public void AQualifiedStrategyIsPreferredOverBalancing()
    {
        // Balanced sampling is for learning about candidates. Something that has earned the right
        // to trade on its own merit should not be held back to keep an experiment tidy.
        var rotation = new StrategyRotation();
        SignalStrategy candidate = Always("a.candidate.v1", "trend");
        SignalStrategy proven = Always("b.proven.v1", "reversion") with
        {
            Qualification = StrategyQualification.Qualified,
        };
        rotation.RecordTrade(proven);
        rotation.RecordTrade(proven);

        Assert.Equal("b.proven.v1", rotation.Select([candidate, proven], Set()).Selection!.Strategy.Id);
    }

    [Fact]
    public void EveryOtherFiringStrategyIsRecordedBecauseAgreementIsEvidence()
    {
        var rotation = new StrategyRotation();

        StrategySelection? selection = rotation.Select(
            [Always("a.v1", "trend"), Always("b.v1", "reversion"), Never("c.v1")], Set()).Selection;

        Assert.Equal("a.v1", selection!.Strategy.Id);
        Assert.Equal(["b.v1"], selection.AlsoFired);
    }

    [Fact]
    public void NothingFiringSelectsNothing()
    {
        Assert.Null(new StrategyRotation().Select([Never("a.v1")], Set()).Selection);
    }

    [Fact]
    public void AThrowingStrategyIsSkippedRatherThanTakingTheLaneDown()
    {
        // A rule that throws on unusual data must not stop the others being asked, and must not be
        // silently counted as having declined either.
        var rotation = new StrategyRotation();
        SignalStrategy broken = Always("a.broken.v1", "trend") with
        {
            Fires = (_, _) => throw new InvalidOperationException("bad rule"),
        };

        StrategyEvaluation evaluation = rotation.Select([broken, Always("b.ok.v1", "trend")], Set());

        Assert.Equal("b.ok.v1", evaluation.Selection!.Strategy.Id);

        // The throw is reported rather than swallowed. It was being caught and skipped, which made
        // a broken rule and a quiet one report the same thing.
        StrategyFault fault = Assert.Single(evaluation.Faults);
        Assert.Equal("a.broken.v1", fault.StrategyId);
        Assert.Equal("bad rule", fault.Reason);
    }

    [Fact]
    public void AStrategyBookThatEveryRuleThrowsInIsAnOutageNotAQuietMarket()
    {
        // The distinction the handbook insists on: FAILED and ABSTAIN are different, because
        // missing evaluation must not masquerade as agreement to stand down. Before this, a lane
        // whose entire rule set threw looked exactly like a lane watching a market with nothing
        // in it, and would have sat quiet indefinitely reporting patience.
        var rotation = new StrategyRotation();
        SignalStrategy broken = Always("a.broken.v1", "trend") with
        {
            Fires = (_, _) => throw new InvalidOperationException("bad rule"),
        };

        StrategyEvaluation evaluation = rotation.Select([broken], Set());

        Assert.Null(evaluation.Selection);
        Assert.True(evaluation.Faulted);
        Assert.Single(evaluation.Faults);
    }

    [Fact]
    public void AQuietMarketIsNotReportedAsAFault()
    {
        var rotation = new StrategyRotation();

        StrategyEvaluation evaluation = rotation.Select([Never("a.quiet.v1")], Set());

        Assert.Null(evaluation.Selection);
        Assert.False(evaluation.Faulted);
        Assert.Empty(evaluation.Faults);
    }

    [Fact]
    public void ARuleThatKeepsThrowingAccumulatesACountSoItStopsLookingLikeOneBadBar()
    {
        // One throw on an odd bar is not interesting. The same rule throwing on every evaluation is
        // a rule that has left the book without anyone deciding to remove it, and only a running
        // tally tells the two apart.
        var rotation = new StrategyRotation();
        SignalStrategy broken = Always("a.broken.v1", "trend") with
        {
            Fires = (_, _) => throw new InvalidOperationException("bad rule"),
        };

        for (int i = 0; i < 3; i++) rotation.Select([broken], Set());

        StrategyFaultTally tally = rotation.FaultCounts()["a.broken.v1"];
        Assert.Equal(3, tally.Count);
        Assert.Equal("bad rule", tally.LastReason);
    }

    [Fact]
    public void SelectionAloneDoesNotCountAsATrade()
    {
        // A selection the risk governor then refused is not a trade, and counting it would push the
        // rotation away from a strategy that has never actually opened a position.
        var rotation = new StrategyRotation();
        rotation.Select([Always("a.v1", "trend")], Set());

        Assert.Empty(rotation.TradeCounts());
    }

    [Fact]
    public void TheClosesOnlySetHoldsOnlyRulesThatNeedNothingElse()
    {
        // The fallback when the feed returns closes without highs, lows and volumes. A rule needing
        // a true range would read zero from synthesised bars, so none of those may appear here.
        IReadOnlyList<SignalStrategy> fallback = SignalStrategies.ClosesOnly(TradedAssetClass.SpotCrypto);

        Assert.NotEmpty(fallback);
        Assert.All(fallback, strategy => Assert.StartsWith("trend.", strategy.Id, StringComparison.Ordinal));
    }

    [Fact]
    public void NoStrategyClaimsToBeQualified()
    {
        // The whole point. Thirteen families were tested and none cleared its own costs at a lower
        // bound; a strategy that has not may be run for evidence but never described as an edge.
        Assert.All(SignalStrategies.ForCrypto,
            s => Assert.Equal(StrategyQualification.Unqualified, s.Qualification));
        Assert.All(SignalStrategies.ForEquity,
            s => Assert.Equal(StrategyQualification.Unqualified, s.Qualification));
    }

    [Fact]
    public void EveryStrategyCarriesTheEvidenceThatDescribesIt()
    {
        // A strategy cannot be discussed without its measured figures, and a lower bound above its
        // mean would mean the numbers had been transcribed wrongly.
        Assert.All(SignalStrategies.ForCrypto.Concat(SignalStrategies.ForEquity), s =>
        {
            Assert.True(double.IsFinite(s.ResearchMeanNetBps));
            Assert.True(s.ResearchLowerBoundBps <= s.ResearchMeanNetBps);
        });
    }

    private static SignalStrategy Always(string id, string mechanism) =>
        new(id, mechanism, StrategyQualification.Unqualified, -10, -20, (_, _) => true);

    private static SignalStrategy Never(string id) =>
        new(id, "trend", StrategyQualification.Unqualified, -10, -20, (_, _) => false);

    private static IndicatorSet Set() =>
        IndicatorSet.Unwarmed([.. Enumerable.Range(0, 40).Select(i => 100m + i)]);
}

/// <summary>
/// Restoring the rotation's balance after a restart.
///
/// The counts lived only in memory, so a day containing several deploys was several independent
/// windows each starting from zero -- and in every one the strategy that fires most often was
/// picked first. The sample tilts while the code still looks like it is balancing.
/// </summary>
public sealed class StrategyRotationRestoreTests
{
    [Fact]
    public void PastTradesAreCountedSoBalancingSurvivesARestart()
    {
        var rotation = new StrategyRotation();
        SignalStrategy busy = Strategy("a.busy.v1", "trend");
        SignalStrategy quiet = Strategy("b.quiet.v1", "reversion");

        rotation.RestoreFrom(["a.busy.v1", "a.busy.v1", "a.busy.v1"], [busy, quiet]);

        Assert.Equal("b.quiet.v1", rotation.Select([busy, quiet], Bars()).Selection!.Strategy.Id);
    }

    [Fact]
    public void RestoringReplacesRatherThanAddsToWhatIsAlreadyCounted()
    {
        // Called at lane start, and a lane can start more than once in a process. Adding would
        // double-count every earlier trade and push the rotation away from a strategy that had
        // only ever traded once.
        var rotation = new StrategyRotation();
        SignalStrategy a = Strategy("a.v1", "trend");
        rotation.RecordTrade(a);

        rotation.RestoreFrom(["a.v1"], [a]);

        Assert.Equal(1, rotation.TradeCounts()["a.v1"]);
    }

    [Fact]
    public void AStrategyThatNoLongerExistsStillCountsItsOwnTrades()
    {
        // It genuinely traded. Its mechanism cannot be balanced against because the strategy is
        // gone, but pretending the trade never happened would misstate the history.
        var rotation = new StrategyRotation();
        SignalStrategy current = Strategy("a.current.v1", "trend");

        rotation.RestoreFrom(["a.retired.v1", "a.current.v1"], [current]);

        Assert.Equal(1, rotation.TradeCounts()["a.retired.v1"]);
        Assert.Equal(1, rotation.TradeCounts()["a.current.v1"]);
    }

    [Fact]
    public void RecordsWithoutAStrategyAreIgnoredRatherThanCountedAsOne()
    {
        var rotation = new StrategyRotation();
        SignalStrategy a = Strategy("a.v1", "trend");

        rotation.RestoreFrom(["", "   ", "a.v1"], [a]);

        Assert.Single(rotation.TradeCounts());
    }

    private static SignalStrategy Strategy(string id, string mechanism) =>
        new(id, mechanism, StrategyQualification.Unqualified, -10, -20, (_, _) => true);

    private static IndicatorSet Bars() =>
        IndicatorSet.Unwarmed([.. Enumerable.Range(0, 40).Select(i => 100m + i)]);
}

/// <summary>
/// A selection that never became a position is not a trade.
///
/// RecordTrade is called on execution rather than on selection precisely so that a candidate the
/// risk governor or the venue refused does not push the rotation away from a strategy that has
/// never actually held anything. Restoring from every durable record undid that guarantee.
/// </summary>
public sealed class RotationCountsOnlyRealTradesTests
{
    [Fact]
    public void AStrategyCreditedOnlyWithRefusedOrdersIsStillTheLeastTraded()
    {
        // The live case: six equity orders the venue rejected out of hours were restored as trades,
        // and the strategy that had never opened a position was pushed to the back of the queue.
        var rotation = new StrategyRotation();
        SignalStrategy refused = Strategy("a.refused.v1", "trend");
        SignalStrategy traded = Strategy("b.traded.v1", "reversion");

        // Only executions that actually filled are restored -- the refused ones never reach here.
        rotation.RestoreFrom(["b.traded.v1", "b.traded.v1"], [refused, traded]);

        Assert.Equal("a.refused.v1", rotation.Select([refused, traded], Bars()).Selection!.Strategy.Id);
        Assert.False(rotation.TradeCounts().ContainsKey("a.refused.v1"));
    }

    private static SignalStrategy Strategy(string id, string mechanism) =>
        new(id, mechanism, StrategyQualification.Unqualified, -10, -20, (_, _) => true);

    private static IndicatorSet Bars() =>
        IndicatorSet.Unwarmed([.. Enumerable.Range(0, 40).Select(i => 100m + i)]);
}

/// <summary>
/// Reserving capacity so one mechanism cannot monopolise the universe.
///
/// Balancing by trade count cannot equalise across opposing mechanisms, because they never compete:
/// a trend rule fires on rising prices and a reversion rule on oversold ones, so they are almost
/// never candidates on the same bar. Trend simply fired first and took all seven crypto symbols for
/// four hours each, and reversion had no capacity left when its own conditions arrived.
/// </summary>
public sealed class MechanismCapacityTests
{
    [Fact]
    public void AMechanismAtItsCapStandsDownForOneThatIsNot()
    {
        var rotation = new StrategyRotation();
        SignalStrategy trend = Strategy("a.trend.v1", "trend");
        SignalStrategy reversion = Strategy("b.reversion.v1", "reversion");
        Dictionary<string, int> open = new(StringComparer.Ordinal) { ["trend"] = 4 };

        StrategySelection? selection =
            rotation.Select([trend, reversion], Bars(), open, maximumPerMechanism: 4).Selection;

        Assert.Equal("b.reversion.v1", selection!.Strategy.Id);
    }

    [Fact]
    public void BelowTheCapTheUsualBalancingStillDecides()
    {
        var rotation = new StrategyRotation();
        SignalStrategy busy = Strategy("a.busy.v1", "trend");
        SignalStrategy quiet = Strategy("b.quiet.v1", "reversion");
        rotation.RecordTrade(busy);
        Dictionary<string, int> open = new(StringComparer.Ordinal) { ["trend"] = 1 };

        Assert.Equal("b.quiet.v1",
            rotation.Select([busy, quiet], Bars(), open, maximumPerMechanism: 4).Selection!.Strategy.Id);
    }

    [Fact]
    public void EveryFiringMechanismBeingFullMeansAbstaining()
    {
        // Nothing is forced through. The capacity genuinely is spoken for, and opening anyway would
        // defeat the reservation the cap exists to make.
        var rotation = new StrategyRotation();
        Dictionary<string, int> open = new(StringComparer.Ordinal) { ["trend"] = 4 };

        Assert.Null(rotation.Select(
            [Strategy("a.trend.v1", "trend")], Bars(), open, maximumPerMechanism: 4).Selection);
    }

    [Fact]
    public void WithNoCapSuppliedNothingIsHeldBack()
    {
        // The default stays permissive, so a caller that does not track capacity is not silently
        // given a different allocation policy than it asked for.
        var rotation = new StrategyRotation();

        Assert.NotNull(rotation.Select([Strategy("a.trend.v1", "trend")], Bars()).Selection);
    }

    private static SignalStrategy Strategy(string id, string mechanism) =>
        new(id, mechanism, StrategyQualification.Unqualified, -10, -20, (_, _) => true);

    private static IndicatorSet Bars() =>
        IndicatorSet.Unwarmed([.. Enumerable.Range(0, 40).Select(i => 100m + i)]);
}

/// <summary>
/// Separating "unproven" from "measured to lose".
///
/// Every strategy is Unqualified, which says only that none has demonstrated an edge. It says
/// nothing about how badly each is measured to do, and those are different situations: a family
/// indistinguishable from breakeven is worth a little to learn about; one measured at minus sixty
/// basis points against a sixty-eight basis point round trip is a repeatable loss with no
/// information left to buy.
/// </summary>
public sealed class KnownLoserTests
{
    [Fact]
    public void EveryCryptoStrategyIsKnownToLoseSoTheLaneStopsOpeningPositions()
    {
        // The live confirmation: nine round trips, $1,799 of notional, $9.00 of round-trip fees,
        // and an account down $6.57. Gross price movement was approximately nothing -- the fee was
        // the entire loss, exactly as the research predicted.
        Assert.All(SignalStrategies.ForCrypto, s => Assert.True(s.IsKnownToLose()));
        Assert.Empty(SignalStrategies.Tradable(TradedAssetClass.SpotCrypto));
    }

    [Fact]
    public void EquityStrategiesNearBreakevenSurviveBecauseTheCostIsAnOrderOfMagnitudeLower()
    {
        // At roughly eight basis points a round trip rather than sixty-eight, the same absent edge
        // costs far less to keep observing, and several families sit close enough to breakeven that
        // the sample genuinely cannot tell them from flat.
        Assert.NotEmpty(SignalStrategies.Tradable(TradedAssetClass.UsEquity));
    }

    [Fact]
    public void AStrategyMeasuredWellBelowZeroIsBlocked()
    {
        Assert.True(Strategy(mean: -60, lower: -70).IsKnownToLose());
    }

    [Fact]
    public void AStrategyWithinItsOwnErrorOfBreakevenIsStillWorthObserving()
    {
        // Mean -1.5 with a standard error near 3.6: the sample cannot distinguish this from flat,
        // which is a reason to keep looking rather than a reason to stop.
        Assert.False(Strategy(mean: -1.5, lower: -8.6).IsKnownToLose());
    }

    [Fact]
    public void APositiveMeanIsNeverBlocked()
    {
        Assert.False(Strategy(mean: 3.3, lower: -13.6).IsKnownToLose());
    }

    [Fact]
    public void AQualifiedStrategyTradesOnItsOwnMeritWhateverTheResearchMeanSays()
    {
        // Qualification means live evidence has superseded the backtest. Blocking on a stale
        // research figure would override the better evidence with the worse one.
        SignalStrategy proven = Strategy(mean: -60, lower: -70) with
        {
            Qualification = StrategyQualification.Qualified,
        };

        Assert.False(proven.IsKnownToLose());
    }

    private static SignalStrategy Strategy(double mean, double lower) =>
        new("x.v1", "trend", StrategyQualification.Unqualified, mean, lower, (_, _) => true);
}
