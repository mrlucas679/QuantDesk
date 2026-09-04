using QuantDesk.Domain.Trading;
using QuantDesk.Runtime.Indicators;

namespace QuantDesk.Runtime.Tests.Indicators;

/// <summary>
/// The book that makes an equity trade possible, and therefore a short possible.
///
/// Across 262 filled orders this system had never once sold short, and the reason was not the short
/// wiring -- that has been complete for a day. It was a chain nobody had traced end to end:
///
///   five-minute bars -> every equity figure negative -> the equity book is empty
///   -> equities never signal -> only crypto trades
///   -> crypto has no borrow at this venue -> every position is long
///
/// Rescanned on thirty-minute bars, three equity families clear their cost at the 95% lower bound.
/// The mechanisms did not change; the sampling interval did, and a rule on five-minute bars pays the
/// same fixed toll six times as often.
/// </summary>
public sealed class EquityHalfHourBookTests
{
    [Fact]
    public void TheFiveMinuteEquityBookHasNothingWorthTrading()
    {
        // The state that made every trade long, asserted rather than described. If this ever starts
        // failing, the five-minute figures have been re-measured and this whole book needs revisiting.
        Assert.Empty(SignalStrategies.Tradable(TradedAssetClass.UsEquity, shadow: null));
    }

    [Fact]
    public void TheHalfHourEquityBookHasRulesThatClearTheirCost()
    {
        IReadOnlyList<SignalStrategy> tradable = SignalStrategies.Tradable(
            TradedAssetClass.UsEquity, barDurationMinutes: 30, shadow: null);

        Assert.NotEmpty(tradable);
        Assert.All(tradable, strategy => Assert.True(strategy.ResearchLowerBoundBps > 0));
    }

    [Fact]
    public void EveryRuleDeclaresTheBarItsFiguresWereMeasuredOn()
    {
        // The bar is part of the rule's identity for the same reason it is part of a fitted model's
        // support domain: the mechanism is the same at five minutes and at thirty, and the measured
        // edge is not.
        Assert.All(
            SignalStrategies.ForEquityHalfHour,
            strategy => Assert.Equal(30, strategy.BarDurationMinutes));

        Assert.All(
            SignalStrategies.ForEquity,
            strategy => Assert.Equal(5, strategy.BarDurationMinutes));
    }

    [Fact]
    public void TheHalfHourFiguresAreNeverServedToAFiveMinuteLane()
    {
        // Serving a number earned under one sampling interval to a lane running on another is the
        // defect the model support domain exists to prevent, arriving through the rule registry.
        IReadOnlyList<string> fiveMinute =
            [.. SignalStrategies.For(TradedAssetClass.UsEquity, 5).Select(rule => rule.Id)];

        Assert.Equal(SignalStrategies.ForEquity.Count, fiveMinute.Count);
        Assert.DoesNotContain(
            SignalStrategies.For(TradedAssetClass.UsEquity, 5),
            rule => rule.BarDurationMinutes == 30);
    }

    [Fact]
    public void CryptoIsUnaffectedByTheEquityClock()
    {
        // Crypto keeps its own book at either bar. It has no survivor at any timeframe scanned, and
        // no borrow at this venue, so nothing here changes what it does.
        Assert.Equal(
            SignalStrategies.For(TradedAssetClass.SpotCrypto, 5).Count,
            SignalStrategies.For(TradedAssetClass.SpotCrypto, 30).Count);
    }

    [Fact]
    public void AHalfHourEquityRuleCanExpressAShort()
    {
        // The end of the chain. A tradable equity rule that can return Short is the first point at
        // which this system is capable of taking a position that is not long.
        IndicatorSet falling = Falling();
        IReadOnlyList<SignalStrategy> tradable = SignalStrategies.Tradable(
            TradedAssetClass.UsEquity, barDurationMinutes: 30, shadow: null);

        Assert.Contains(
            tradable,
            rule => rule.Fires(falling, falling.Length - 1) is SignalDirection.Short);
    }

    /// <summary>A steady decline, which is what a bearish market looks like to every rule here.</summary>
    private static IndicatorSet Falling()
    {
        List<decimal> closes = [.. Enumerable.Range(0, 400).Select(i => 400m - (0.35m * i))];
        IndicatorSet? set = IndicatorSet.Build(
            closes,
            [.. closes.Select(c => c + 0.6m)],
            [.. closes.Select(c => c - 0.6m)],
            [.. Enumerable.Repeat(1_000m, closes.Count)]);

        Assert.NotNull(set);
        return set;
    }
}
