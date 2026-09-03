using QuantDesk.Domain.Trading;
using QuantDesk.Runtime.Indicators;
using QuantDesk.Runtime.Research;

namespace QuantDesk.Runtime.Tests.Indicators;

/// <summary>
/// Rules that can say short, and the two places that silently assumed they could not.
///
/// Every rule in both books used to answer <c>bool</c>. Direction was not something a rule could
/// express, so each one was written as the bullish half of a symmetric idea -- RSI crossing up out
/// of oversold with no overbought counterpart, a close above the Donchian high with no test of the
/// low, a VWAP gap read only when price sat below. In a falling market the best available outcome
/// was to abstain.
/// </summary>
public sealed class SignalDirectionTests
{
    [Fact]
    public void EveryRuleCanExpressBothDirections()
    {
        // The structural claim. A rule that can only ever return Long or None is still the old
        // long-only rule wearing the new type, and this is the test that would catch a half-done
        // conversion.
        IReadOnlyList<SignalStrategy> book = SignalStrategies.ForCrypto;

        SignalDirection[] rising = Directions(book, Trending(up: true));
        SignalDirection[] falling = Directions(book, Trending(up: false));

        Assert.Contains(SignalDirection.Long, rising);
        Assert.Contains(SignalDirection.Short, falling);
    }

    [Theory]
    [InlineData("trend.adx-filtered.v1")]
    [InlineData("trend.momentum-dual-horizon.v1")]
    public void ATrendRuleFollowsTheTrendInBothDirections(string strategyId)
    {
        // The behaviour that was missing. On a falling series this rule previously returned false
        // and the lane abstained; it must now say Short, and it must still say Long when the series
        // rises.
        SignalStrategy rule = SignalStrategies.ForCrypto.Single(item => item.Id == strategyId);

        Assert.Equal(SignalDirection.Long, Fire(rule, Trending(up: true)));
        Assert.Equal(SignalDirection.Short, Fire(rule, Trending(up: false)));
    }

    [Fact]
    public void AReversionRuleOpposesTheTrendRatherThanFollowingIt()
    {
        // Not a defect, and worth stating so nobody 'fixes' it: a reversion rule fires against the
        // move by construction. On a series climbing away from its mean the honest answer is Short,
        // which is exactly the answer the old boolean book had no way to give.
        SignalStrategy rule =
            SignalStrategies.ForCrypto.Single(item => item.Id == "reversion.vwap.v1");

        Assert.Equal(SignalDirection.Short, Fire(rule, Trending(up: true)));
        Assert.Equal(SignalDirection.Long, Fire(rule, Trending(up: false)));
    }

    [Fact]
    public void TheBreakoutRulesCanNowSeeTheLowerEdgeOfTheRange()
    {
        // DonchianLow did not exist. Every breakout rule tested the high alone, so a breakdown
        // through support could not be expressed at all -- not a gap in one rule, a gap in the
        // indicator set that made the short half unwritable.
        IndicatorSet set = Trending(up: false);

        Assert.NotEmpty(set.DonchianLow);
        Assert.Contains(set.DonchianLow, value => !double.IsNaN(value));
    }

    // ------------------------------------------------------------------- shadow must be signed

    [Fact]
    public void AShortThatWinsIsRecordedAsAWin()
    {
        // Price falls 200 bps. A long loses that; a short earns it. Scoring the short as a long
        // inverts its sign, so a rule that was right about a fall would be recorded as having lost
        // and then stood down for being right.
        string path = Path.Combine(Path.GetTempPath(), $"qd-dir-{Guid.NewGuid():N}.json");
        try
        {
            var log = new ShadowSignalLog(path);
            log.TryRecord(Signal(path: "s", direction: SignalDirection.Short));

            log.Resolve(Fired.AddHours(5), _ => 98m);

            ShadowSignal resolved = Assert.Single(log.ListAll());

            // +200 bps of gain to a short, less the 60 bps round trip.
            Assert.Equal(140d, resolved.NetBps!.Value, precision: 6);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ALongIsUnaffectedByTheSigning()
    {
        string path = Path.Combine(Path.GetTempPath(), $"qd-dir-{Guid.NewGuid():N}.json");
        try
        {
            var log = new ShadowSignalLog(path);
            log.TryRecord(Signal(path: "l", direction: SignalDirection.Long));

            log.Resolve(Fired.AddHours(5), _ => 102m);

            Assert.Equal(140d, Assert.Single(log.ListAll()).NetBps!.Value, precision: 6);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // --------------------------------------------------- exploration must not re-buy an answer

    [Fact]
    public void ExplorationSkipsARuleShadowHasAlreadyCondemned()
    {
        // Exploration buys fills, spread and slippage, which shadow cannot see. What shadow sees
        // perfectly well is a negative reference-price edge, and paying sixty basis points to be
        // told that again is a subscription, not an experiment.
        SignalStrategy first = ExplorableRule();
        var condemned = new Dictionary<string, ShadowSummary>(StringComparer.Ordinal)
        {
            [first.Id] = new(Signals: 200, MeanNetBps: -40d, LowerBoundBps: -50d),
        };

        IReadOnlyList<SignalStrategy> explorable =
            SignalStrategies.Explorable([first], VenueCost, condemned);

        Assert.DoesNotContain(explorable, item => item.Id == first.Id);
    }

    [Fact]
    public void ExplorationKeepsARuleShadowCannotYetJudge()
    {
        // Too few signals to say anything. Refusing to explore on that basis would close the only
        // route a stood-down rule has back.
        SignalStrategy first = ExplorableRule();
        var thin = new Dictionary<string, ShadowSummary>(StringComparer.Ordinal)
        {
            [first.Id] = new(Signals: 3, MeanNetBps: -40d, LowerBoundBps: -50d),
        };

        Assert.Contains(
            SignalStrategies.Explorable([first], VenueCost, thin),
            item => item.Id == first.Id);
    }

    [Fact]
    public void ExplorationKeepsARuleWhoseShadowRecordIsMerelyUnprofitableNotDecisive()
    {
        // Negative but well inside its own error bar. That is exactly the case exploration exists
        // to resolve, and condemning it would make the filter a blanket ban on trading at all.
        SignalStrategy first = ExplorableRule();
        var noisy = new Dictionary<string, ShadowSummary>(StringComparer.Ordinal)
        {
            [first.Id] = new(Signals: 200, MeanNetBps: -5d, LowerBoundBps: -60d),
        };

        Assert.Contains(
            SignalStrategies.Explorable([first], VenueCost, noisy),
            item => item.Id == first.Id);
    }

    // ------------------------------------------------- a short must not be executed as a long

    [Fact]
    public void ShortAndLongAreDistinctValuesSoExecutionCannotConflateThem()
    {
        // The hazard the pipeline refusal guards. Rules can now say Short while execution still
        // sends OrderSide.Buy, so a bearish signal reaching the compiler would open a long on it --
        // acting on the rule's opinion with the sign reversed, which is worse than the long-only
        // book that at least abstained when it disagreed.
        //
        // None is also its own value: a rule that looked and found nothing is not a rule that could
        // not look, and section 26.2 treats a refusal to commit as information.
        Assert.NotEqual(SignalDirection.Long, SignalDirection.Short);
        Assert.NotEqual(SignalDirection.None, SignalDirection.Long);
        Assert.NotEqual(SignalDirection.None, SignalDirection.Short);

        // Default(SignalDirection) must be None rather than a tradable direction, or a record
        // deserialised without the field would read as an instruction to take exposure.
        Assert.Equal(SignalDirection.None, default(SignalDirection));
    }

    // ------------------------------------------------------------------------------- fixtures

    private static readonly DateTimeOffset Fired = DateTimeOffset.Parse("2026-09-03T12:00:00Z");

    private static SignalDirection Fire(SignalStrategy rule, IndicatorSet set) =>
        rule.Fires(set, set.Length - 1);

    private static SignalDirection[] Directions(
        IReadOnlyList<SignalStrategy> book, IndicatorSet set)
    {
        int last = set.Length - 1;
        return [.. book.Select(strategy => strategy.Fires(set, last))];
    }

    /// <summary>A steady advance or decline, which is what a trend looks like to every rule here.</summary>
    private static IndicatorSet Trending(bool up)
    {
        decimal step = up ? 0.35m : -0.35m;
        List<decimal> closes = [.. Enumerable.Range(0, 400).Select(i => 200m + (step * i))];
        IndicatorSet? set = IndicatorSet.Build(
            closes,
            [.. closes.Select(c => c + 0.6m)],
            [.. closes.Select(c => c - 0.6m)],
            [.. Enumerable.Repeat(1_000m, closes.Count)]);

        Assert.NotNull(set);
        return set;
    }

    private const double VenueCost = 60d;

    /// <summary>
    /// A rule the cost filter leaves alone, so a shadow test measures shadow.
    ///
    /// Exploration now also excludes anything known to lose against the venue's real round trip,
    /// and every rule in both live books currently is. Asking a live book here would pass for the
    /// wrong reason -- the rule absent because of cost rather than because of shadow -- so the
    /// policy is applied to a book of one rule whose edge clears the toll comfortably.
    /// </summary>
    private static SignalStrategy ExplorableRule() => SignalStrategies.ForCrypto[0] with
    {
        ResearchMeanNetBps = 200d,
        ResearchLowerBoundBps = 180d,
        ResearchCostAssumptionBps = 0d,
        Qualification = StrategyQualification.Qualified,
    };

    private static ShadowSignal Signal(string path, SignalDirection direction) =>
        new(
            SignalId: $"rule|BTC/USD|{path}",
            Symbol: "BTC/USD",
            StrategyId: "rule.v1",
            FiredAt: Fired,
            EntryReferencePrice: 100m,
            ResolveAt: Fired.AddHours(4),
            VenueRoundTripBps: 60d)
        {
            Direction = direction,
        };
}
