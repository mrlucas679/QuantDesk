using QuantDesk.Runtime.Indicators;

namespace QuantDesk.Runtime.Tests.Indicators;

/// <summary>
/// The measures that are defined against a span of time rather than a count of bars.
///
/// Every one of these was previously computed by counting bars, which is the same thing only while
/// the feed returns an unbroken sequence.
/// </summary>
public sealed class IndicatorTimeAxisTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-01T13:30:00Z");
    private static readonly TimeSpan Bar = TimeSpan.FromMinutes(5);

    [Fact]
    public void AnHourAgoIsAnHourAgoEvenWhenBarsAreMissing()
    {
        // Twelve five-minute bars equal an hour only while nothing is dropped. Here the feed skips
        // an hour in the middle, so counting twelve bars back from the end lands two hours before
        // it rather than one -- and the rule reading it would be measuring a different span than
        // the one it was calibrated on, with nothing to show that it had changed.
        List<DateTimeOffset> stamps = [];
        for (int i = 0; i < 60; i++) stamps.Add(Start + (Bar * i));
        for (int i = 0; i < 60; i++) stamps.Add(Start + TimeSpan.FromHours(6) + (Bar * i));

        IndicatorSet set = Build(stamps);
        int last = set.Length - 1;

        int anHourBack = set.IndexAtOrBefore(last, TimeSpan.FromHours(1), fallbackBars: 12);

        Assert.True(anHourBack >= 0);
        Assert.True(set.Timestamps[last] - set.Timestamps[anHourBack] >= TimeSpan.FromHours(1));

        // And it is close to an hour, not two: the gap sits far enough back not to be crossed.
        Assert.True(set.Timestamps[last] - set.Timestamps[anHourBack] < TimeSpan.FromMinutes(70));
    }

    [Fact]
    public void WithoutATimeAxisTheBarCountIsUsedAndSaysSo()
    {
        // The fallback is the behaviour this had before, kept for a feed that supplies no
        // timestamps. It is an approximation, and HasTimeAxis is how a caller can tell.
        IndicatorSet set = Build(timestamps: null);

        Assert.False(set.HasTimeAxis);
        Assert.Equal(set.Length - 1 - 12, set.IndexAtOrBefore(set.Length - 1, TimeSpan.FromHours(1), 12));
    }

    [Fact]
    public void AHistoryTooShortForTheSpanRefusesRatherThanReachingPastItsStart()
    {
        List<DateTimeOffset> stamps = [];
        for (int i = 0; i < 130; i++) stamps.Add(Start + (Bar * i));

        IndicatorSet set = Build(stamps);

        Assert.Equal(-1, set.IndexAtOrBefore(3, TimeSpan.FromHours(1), fallbackBars: 12));
    }

    [Fact]
    public void SessionVwapResetsAtTheSessionBoundaryRatherThanRollingThroughIt()
    {
        // VWAP is defined as the volume-weighted price so far today. A rolling window is a
        // different measure wearing the same name, and reversion.vwap.v1 -- the best-scoring equity
        // rule in the book -- was measured against the rolling one.
        //
        // Two sessions, the second trading at a visibly higher level. A session-scoped VWAP early
        // in the second session sits near the second session's prices; a rolling one still carries
        // the first session's.
        List<DateTimeOffset> stamps = [];
        List<decimal> closes = [];
        for (int i = 0; i < 80; i++) { stamps.Add(Start + (Bar * i)); closes.Add(100m); }
        DateTimeOffset next = Start.AddDays(1);
        for (int i = 0; i < 80; i++) { stamps.Add(next + (Bar * i)); closes.Add(200m); }

        IndicatorSet session = Build(stamps, closes, sessionScoped: true);
        IndicatorSet rolling = Build(stamps, closes, sessionScoped: false);

        // Ten bars into the second session.
        int i2 = 89;
        Assert.True(session.SessionScoped);
        Assert.True(session.Vwap48[i2] > 190d, $"session vwap was {session.Vwap48[i2]}");
        Assert.True(rolling.Vwap48[i2] < 190d, $"rolling vwap was {rolling.Vwap48[i2]}");
    }

    [Fact]
    public void CryptoIsNotSessionScopedBecauseItHasNoSessionToResetOn()
    {
        List<DateTimeOffset> stamps = [];
        for (int i = 0; i < 160; i++) stamps.Add(Start + (Bar * i));

        Assert.False(Build(stamps, sessionScoped: false).SessionScoped);
    }

    [Fact]
    public void VolumeIsScoredAgainstTheSameTimeOfDayNotAgainstItself()
    {
        // The construction the engineering constitution names as wrong is a volume z-score over a
        // trailing window of the same series. Volume has a strong daily shape, so a trailing window
        // scores every open as a surge. Here every day opens busy and runs quiet: a like-for-like
        // baseline finds nothing unusual in a busy open, and a trailing one finds a surge.
        List<DateTimeOffset> stamps = [];
        List<decimal> volumes = [];
        for (int day = 0; day < 12; day++)
        {
            DateTimeOffset open = Start.AddDays(day);
            for (int i = 0; i < 12; i++)
            {
                stamps.Add(open + (Bar * i));
                volumes.Add(i == 0 ? 10_000m : 1_000m);   // busy open, quiet rest
            }
        }

        IndicatorSet set = Build(stamps, volumes: volumes);

        // The last day's open: routine for an open, and the baseline knows it.
        int lastOpen = stamps.Count - 12;
        Assert.True(set.HasTimeAxis);
        Assert.True(Math.Abs(set.VolumeZ48[lastOpen]) < 1.0,
            $"a routine open scored {set.VolumeZ48[lastOpen]}");
    }

    [Fact]
    public void ABarWithTooFewPriorDaysIsLeftUnscoredRatherThanScoredAgainstItself()
    {
        // Five prior observations at the same time of day are required. Before that the bar is NaN,
        // and a rule reading it declines -- which is the correct answer when the question cannot
        // yet be asked, rather than a number built mostly from the bar itself.
        List<DateTimeOffset> stamps = [];
        for (int day = 0; day < 12; day++)
        {
            DateTimeOffset open = Start.AddDays(day);
            for (int i = 0; i < 12; i++) stamps.Add(open + (Bar * i));
        }

        IndicatorSet set = Build(stamps);

        Assert.True(double.IsNaN(set.VolumeZ48[0]));
        Assert.True(double.IsNaN(set.VolumeZ48[12]));    // second day, still only one prior
    }

    private static IndicatorSet Build(
        IReadOnlyList<DateTimeOffset>? timestamps,
        IReadOnlyList<decimal>? closes = null,
        IReadOnlyList<decimal>? volumes = null,
        bool sessionScoped = false)
    {
        int n = timestamps?.Count ?? closes?.Count ?? 160;
        List<decimal> c = closes is not null
            ? [.. closes]
            : [.. Enumerable.Range(0, n).Select(i => 100m + (i % 7))];
        List<decimal> v = volumes is not null ? [.. volumes] : [.. Enumerable.Repeat(1_000m, n)];
        List<decimal> h = [.. c.Select(x => x + 1m)];
        List<decimal> l = [.. c.Select(x => x - 1m)];

        IndicatorSet? set = IndicatorSet.Build(c, h, l, v, timestamps, sessionScoped);
        Assert.NotNull(set);
        return set;
    }
}
