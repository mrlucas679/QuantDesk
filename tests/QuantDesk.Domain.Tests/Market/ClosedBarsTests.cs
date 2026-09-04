using QuantDesk.Domain.Market;

namespace QuantDesk.Domain.Tests.Market;

/// <summary>
/// Only finished bars may inform a decision.
///
/// The lane asked the venue for bars up to "now" and used every one it got back, the forming bar
/// included. A decision taken at 13:57 was computed from a 13:45-14:00 candle that was three
/// minutes from finished: a close that was not the close, a high and low still moving, and a volume
/// that was a fraction of the bar's eventual total.
///
/// The re-evaluation cadence is what turns that from an inaccuracy into a defect. The lane looks
/// every few seconds, so one candle yields a different answer on every pass as it fills in -- a rule
/// can fire, stop firing and fire again inside a single bar, and whichever pass caught the extreme
/// is the one that opened the position.
/// </summary>
public sealed class ClosedBarsTests
{
    private static readonly TimeSpan FiveMinutes = TimeSpan.FromMinutes(5);
    private static readonly DateTimeOffset Open = new(2026, 9, 4, 13, 0, 0, TimeSpan.Zero);

    /// <summary>Bars opening at 13:00, 13:05, ... Alpaca stamps a bar with its opening time.</summary>
    private static DateTimeOffset[] Bars(int count) =>
        [.. Enumerable.Range(0, count).Select(i => Open.AddMinutes(5 * i))];

    [Fact]
    public void TheFormingBarIsNotCounted()
    {
        // Four bars opening 13:00-13:15. At 13:17 the 13:15 bar has three minutes left to run.
        Assert.Equal(3, ClosedBars.CompletedCount(Bars(4), FiveMinutes, Open.AddMinutes(17)));
    }

    [Fact]
    public void ABarIsCompleteTheInstantItsPeriodElapses()
    {
        // 13:15 + 5m = 13:20. At exactly 13:20 the bar is finished, not still forming: the interval
        // a bar covers is [t, t + duration), so the boundary belongs to the next one.
        Assert.Equal(4, ClosedBars.CompletedCount(Bars(4), FiveMinutes, Open.AddMinutes(20)));
        Assert.Equal(3, ClosedBars.CompletedCount(Bars(4), FiveMinutes, Open.AddMinutes(19.99)));
    }

    [Fact]
    public void AStaleSeriesIsEntirelyComplete()
    {
        // A feed that has stopped delivering is a different problem, and not this function's to
        // report. Every bar it did deliver has finished.
        Assert.Equal(4, ClosedBars.CompletedCount(Bars(4), FiveMinutes, Open.AddHours(9)));
    }

    [Fact]
    public void ASeriesWhereNothingHasClosedYetCountsNothing()
    {
        Assert.Equal(0, ClosedBars.CompletedCount([Open], FiveMinutes, Open.AddMinutes(1)));
    }

    [Fact]
    public void AnEmptySeriesIsNotAnError()
    {
        Assert.Equal(0, ClosedBars.CompletedCount([], FiveMinutes, Open));
        Assert.True(ClosedBars.NewestIsClosed([], FiveMinutes, Open));
    }

    [Fact]
    public void TheSameCandleGivesTheSameAnswerThroughoutItsLife()
    {
        // The regression that matters. Sampling the same series at three moments inside one forming
        // bar must return one count, not three -- that identity is what stops a rule firing,
        // un-firing and re-firing as a candle fills in.
        DateTimeOffset[] bars = Bars(4);

        int[] counts =
        [
            ClosedBars.CompletedCount(bars, FiveMinutes, Open.AddMinutes(15.1)),
            ClosedBars.CompletedCount(bars, FiveMinutes, Open.AddMinutes(17.5)),
            ClosedBars.CompletedCount(bars, FiveMinutes, Open.AddMinutes(19.9)),
        ];

        Assert.All(counts, count => Assert.Equal(3, count));
    }

    [Fact]
    public void ACoarserBarTakesLongerToClose()
    {
        // A thirty-minute strategy on a five-second evaluation loop takes 360 looks at one candle.
        // The bar duration is the thing that decides how many of those are looking at a partial.
        DateTimeOffset[] bars = [Open, Open.AddMinutes(30)];

        Assert.Equal(1, ClosedBars.CompletedCount(bars, TimeSpan.FromMinutes(30), Open.AddMinutes(45)));
        Assert.Equal(2, ClosedBars.CompletedCount(bars, TimeSpan.FromMinutes(30), Open.AddMinutes(60)));
    }

    [Fact]
    public void NewestIsClosedAnswersWhetherThisIsABarBoundary()
    {
        DateTimeOffset[] bars = Bars(4);

        Assert.False(ClosedBars.NewestIsClosed(bars, FiveMinutes, Open.AddMinutes(17)));
        Assert.True(ClosedBars.NewestIsClosed(bars, FiveMinutes, Open.AddMinutes(20)));
    }

    [Fact]
    public void AnUnknownBarDurationDropsNothingRatherThanEverything()
    {
        // A caller that cannot say how long its bars are gets the series unchanged. Discarding on
        // an unknown period would silently blind the lane; keeping it leaves the previous behaviour,
        // which is the conservative direction for a value nobody supplied.
        Assert.Equal(4, ClosedBars.CompletedCount(Bars(4), TimeSpan.Zero, Open));
    }
}
