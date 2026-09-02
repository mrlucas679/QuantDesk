using QuantDesk.Runtime.Indicators;

namespace QuantDesk.Runtime.Tests.Indicators;

/// <summary>
/// Telling a feature that could not be computed apart from a market that did nothing.
///
/// An entirely NaN series behaves exactly like one that is still warming up: every rule reading it
/// declines, quietly and forever. Nothing anywhere says the feature is missing rather than the
/// market being quiet, and the research plane -- which has sixty days of history -- computes the
/// same feature happily. That divergence is the hardest class of defect to attribute afterwards.
/// </summary>
public sealed class IndicatorAvailabilityTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
    private static readonly TimeSpan Bar = TimeSpan.FromMinutes(5);

    [Fact]
    public void TwentyHoursOfHistoryCannotSupportATimeOfDayBaselineAndSaysSo()
    {
        // Exactly what production had. The crypto client fetches twenty-four hours and retains 240
        // bars -- measured at 19h55m -- so every time-of-day bucket held one observation and
        // VolumeZ48 was NaN for every bar, with nothing reporting it.
        IndicatorSet set = Build(hours: 20);

        Assert.False(set.IsAvailable("VolumeZ48"));
        Assert.Contains(set.Unavailable, item => item.Contains("VolumeZ48"));
        Assert.Contains(set.Unavailable, item => item.Contains("time-of-day baseline"));

        // And the series really is empty, which is the point: the report is not decorative.
        Assert.All(set.VolumeZ48, value => Assert.False(double.IsFinite(value)));
    }

    [Fact]
    public void SixDaysOfHistoryIsEnoughAndNothingIsReportedMissing()
    {
        IndicatorSet set = Build(hours: 24 * (IndicatorSet.MinimumPriorDaysForTimeOfDay + 2));

        Assert.True(set.IsAvailable("VolumeZ48"));
        Assert.Empty(set.Unavailable);
        Assert.Contains(set.VolumeZ48, double.IsFinite);
    }

    [Fact]
    public void AFeedWithNoTimeAxisReportsNothingBecauseItCannotKnow()
    {
        // Without timestamps the volume z-score falls back to the trailing window, which is
        // computable from any history. Claiming a shortfall there would be inventing one.
        IReadOnlyList<decimal> closes = [.. Enumerable.Range(0, 200).Select(i => 100m + (i % 7))];
        IndicatorSet set = IndicatorSet.Build(
            closes,
            [.. closes.Select(c => c + 1m)],
            [.. closes.Select(c => c - 1m)],
            [.. Enumerable.Repeat(1_000m, closes.Count)])!;

        Assert.Empty(set.Unavailable);
    }

    [Fact]
    public void OnlyTheRulesThatActuallyNeedASeriesDeclareIt()
    {
        // The check that keeps the reason honest. Reporting "IndicatorUnavailable" whenever any
        // series is missing would claim a cause that has not been established -- the market may
        // simply be quiet -- so the lane only reports it when a rule it could have traded needs
        // the missing series.
        Assert.Contains(
            SignalStrategies.ForCrypto.Single(s => s.Id == "volume.surge-breakout.v1").RequiredSeries,
            series => series == "VolumeZ48");

        Assert.Empty(
            SignalStrategies.ForCrypto.Single(s => s.Id == "breakout.bollinger-upper.v1").RequiredSeries);
    }

    private static IndicatorSet Build(int hours)
    {
        int n = (int)(TimeSpan.FromHours(hours) / Bar);
        List<DateTimeOffset> stamps = [.. Enumerable.Range(0, n).Select(i => Start + (Bar * i))];
        List<decimal> closes = [.. Enumerable.Range(0, n).Select(i => 100m + (i % 7))];

        IndicatorSet? set = IndicatorSet.Build(
            closes,
            [.. closes.Select(c => c + 1m)],
            [.. closes.Select(c => c - 1m)],
            [.. Enumerable.Range(0, n).Select(i => 1_000m + (i % 11))],
            stamps);

        Assert.NotNull(set);
        return set;
    }
}
