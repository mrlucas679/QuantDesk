using QuantDesk.Runtime.Telemetry;

namespace QuantDesk.Runtime.Tests.Telemetry;

/// <summary>
/// Reporting the distribution of a stage's latency rather than its mean.
///
/// Section 24.1 says average latency alone is insufficient, and the reason is that a mean hides
/// exactly the behaviour that causes harm: the slow cycles are rare by definition. A path that is
/// usually 40ms and occasionally four seconds averages to something unremarkable and is a serious
/// problem -- during those four seconds the quote the decision was made on is stale, which is the
/// condition the entry fence exists to catch.
/// </summary>
public sealed class LatencyRecorderTests
{
    [Fact]
    public void ARareSlowObservationShowsInTheTailAndNotInTheMedian()
    {
        // The whole argument for percentiles in one test. Ninety-nine fast cycles and one very slow
        // one: the median says the path is fast, the maximum says it is not, and both are true.
        var recorder = new LatencyRecorder();
        for (int i = 0; i < 99; i++) recorder.Record(LatencyStage.Decision, 40d);
        recorder.Record(LatencyStage.Decision, 4_000d);

        LatencySummary summary = Summary(recorder, LatencyStage.Decision);

        Assert.Equal(40d, summary.P50, precision: 6);
        Assert.Equal(4_000d, summary.Maximum, precision: 6);
    }

    [Fact]
    public void PercentilesRiseWithTheQuantile()
    {
        var recorder = new LatencyRecorder();
        for (int i = 1; i <= 100; i++) recorder.Record(LatencyStage.MarketDataFetch, i);

        LatencySummary summary = Summary(recorder, LatencyStage.MarketDataFetch);

        Assert.True(summary.P50 <= summary.P95);
        Assert.True(summary.P95 <= summary.P99);
        Assert.True(summary.P99 <= summary.Maximum);
    }

    [Fact]
    public void EveryReportedPercentileIsAnObservationThatActuallyHappened()
    {
        // Nearest rank rather than interpolation. An interpolated p99 invents a value between two
        // real observations, and the point of a tail statistic is to name something that occurred.
        var recorder = new LatencyRecorder();
        foreach (double value in new[] { 10d, 20d, 30d, 40d, 50d })
            recorder.Record(LatencyStage.Reservation, value);

        LatencySummary summary = Summary(recorder, LatencyStage.Reservation);

        Assert.Contains(summary.P50, new[] { 10d, 20d, 30d, 40d, 50d });
        Assert.Contains(summary.P99, new[] { 10d, 20d, 30d, 40d, 50d });
    }

    [Fact]
    public void TheWindowIsBoundedAndKeepsTheRecentPast()
    {
        // The constitution forbids an unbounded collection anywhere, and a latency recorder is the
        // classic place one appears: keeping every observation means a week-long process spends its
        // memory remembering how fast it used to be.
        var recorder = new LatencyRecorder();
        for (int i = 0; i < LatencyRecorder.WindowSize * 3; i++)
            recorder.Record(LatencyStage.EvaluationCycle, 1d);

        Assert.Equal(LatencyRecorder.WindowSize, Summary(recorder, LatencyStage.EvaluationCycle).Count);
    }

    [Fact]
    public void OldObservationsFallOutOfTheWindow()
    {
        // A problem three days ago must not still be shaping today's p99.
        var recorder = new LatencyRecorder();
        for (int i = 0; i < LatencyRecorder.WindowSize; i++)
            recorder.Record(LatencyStage.Decision, 5_000d);
        for (int i = 0; i < LatencyRecorder.WindowSize; i++)
            recorder.Record(LatencyStage.Decision, 10d);

        Assert.Equal(10d, Summary(recorder, LatencyStage.Decision).Maximum, precision: 6);
    }

    [Fact]
    public void AClockArtefactIsRefusedRatherThanRecordedAsAFastCycle()
    {
        // A negative or non-finite duration would drag every percentile toward a number nothing
        // actually took.
        var recorder = new LatencyRecorder();
        recorder.Record(LatencyStage.Decision, -5d);
        recorder.Record(LatencyStage.Decision, double.NaN);
        recorder.Record(LatencyStage.Decision, double.PositiveInfinity);

        Assert.Empty(recorder.Summarise());
    }

    [Fact]
    public void StagesAreReportedSeparately()
    {
        // A cycle slow because the venue is slow and one slow because this system is slow need
        // different responses, and one number cannot tell them apart.
        var recorder = new LatencyRecorder();
        recorder.Record(LatencyStage.MarketDataFetch, 780d);
        recorder.Record(LatencyStage.Decision, 20d);

        Assert.Equal(780d, Summary(recorder, LatencyStage.MarketDataFetch).P50, precision: 6);
        Assert.Equal(20d, Summary(recorder, LatencyStage.Decision).P50, precision: 6);
    }

    [Fact]
    public void AStageNeverObservedIsNotReportedAsZero()
    {
        // Zero would read as instantaneous rather than absent, and an operator would draw the
        // opposite conclusion from the one the data supports.
        var recorder = new LatencyRecorder();
        recorder.Record(LatencyStage.Decision, 20d);

        Assert.DoesNotContain(recorder.Summarise(), s => s.Stage == LatencyStage.BrokerSubmit);
    }

    [Fact]
    public void RuntimeHealthReportsWhatTheRuntimeAlreadyCounts()
    {
        // Section 30.3 makes "memory plateaus under soak after warm-up" a release gate, and the
        // only way to know whether it plateaus is to watch it.
        RuntimeHealth health = RuntimeHealthProbe.Read();

        Assert.True(health.ManagedHeapBytes > 0, $"heap={health.ManagedHeapBytes}");
        Assert.True(health.Gen0Collections >= 0, $"gen0={health.Gen0Collections}");
        Assert.True(health.UptimeSeconds >= 0, $"uptime={health.UptimeSeconds}");

        // Non-negative rather than positive: ThreadPool.ThreadCount legitimately reads zero before
        // the pool has run any work, which a test host can hit. Asserting otherwise would encode a
        // property the runtime does not promise.
        Assert.True(health.ThreadPoolThreads >= 0, $"threads={health.ThreadPoolThreads}");
        Assert.True(health.PendingWorkItems >= 0, $"pending={health.PendingWorkItems}");
    }

    private static LatencySummary Summary(LatencyRecorder recorder, LatencyStage stage) =>
        recorder.Summarise().Single(summary => summary.Stage == stage);
}
