using System.Diagnostics;

namespace QuantDesk.Runtime.Telemetry;

/// <summary>The stages of the decision path that are timed separately.</summary>
/// <remarks>
/// Separately because a single end-to-end number cannot be acted on. "The cycle took 800ms" tells
/// an operator nothing; "the broker took 780ms of it" tells them where to look and whether it is
/// theirs to fix.
/// </remarks>
public enum LatencyStage
{
    /// <summary>Fetching bars and quotes from the venue.</summary>
    MarketDataFetch,

    /// <summary>Computing the indicator set from those bars.</summary>
    FeatureBuild,

    /// <summary>Evaluating strategies, committee, costs and risk.</summary>
    Decision,

    /// <summary>Reading the resting book.</summary>
    OrderBookFetch,

    /// <summary>Taking the durable reservation before anything is sent.</summary>
    Reservation,

    /// <summary>The broker round trip on submission.</summary>
    BrokerSubmit,

    /// <summary>One full evaluation of one instrument, end to end.</summary>
    EvaluationCycle,
}

/// <param name="Stage">Which stage.</param>
/// <param name="Count">Observations in the window.</param>
/// <param name="P50">Median milliseconds.</param>
/// <param name="P95">95th percentile milliseconds.</param>
/// <param name="P99">99th percentile milliseconds.</param>
/// <param name="Maximum">Worst observation in the window, in milliseconds.</param>
public readonly record struct LatencySummary(
    LatencyStage Stage, int Count, double P50, double P95, double P99, double Maximum);

/// <summary>
/// Records how long each stage of the decision path takes, and reports the distribution rather
/// than the mean.
///
/// Why percentiles and not an average
/// ----------------------------------
/// Section 24.1 says it directly: average latency alone is insufficient. A mean hides exactly the
/// behaviour that matters, because the slow requests are the ones that cause harm and they are by
/// definition rare. A cycle that is usually 40ms and occasionally 4 seconds averages to something
/// unremarkable and is a serious problem -- during those 4 seconds the quote the decision was made
/// on is stale, and the entry fence exists precisely because acting on a stale price is expensive.
///
/// The p99 is also the number that says whether a deadline is real. This system has a 30 bps
/// adverse-move fence between deciding and submitting; whether that fence protects anything depends
/// on how long that gap actually is, and until now nothing measured it.
///
/// Bounded by construction
/// -----------------------
/// A fixed ring per stage, sized once and never grown. The constitution forbids an unbounded
/// collection anywhere, and a latency recorder is the classic place one appears: it is tempting to
/// keep every observation, and a process that runs for a week would then spend its memory on
/// remembering how fast it used to be. The window is the recent past, which is the only part anyone
/// acts on.
///
/// Recording is a timestamp subtraction and one array write under a short lock. No allocation, no
/// LINQ, nothing that can throw.
/// </summary>
public sealed class LatencyRecorder
{
    /// <summary>
    /// Observations retained per stage.
    ///
    /// 512, which at one evaluation per instrument per cycle is several hours of history for a
    /// universe this size -- long enough to see a degradation build, short enough that a problem
    /// three days ago is not still shaping the p99 today.
    /// </summary>
    public const int WindowSize = 512;

    private readonly Dictionary<LatencyStage, Window> _windows = [];
    private readonly Lock _gate = new();

    public LatencyRecorder()
    {
        foreach (LatencyStage stage in Enum.GetValues<LatencyStage>()) _windows[stage] = new Window();
    }

    /// <summary>Times a block and records it, returning the elapsed milliseconds.</summary>
    public double Record(LatencyStage stage, long startTimestamp)
    {
        double elapsed =
            (Stopwatch.GetTimestamp() - startTimestamp) * 1_000d / Stopwatch.Frequency;
        Record(stage, elapsed);
        return elapsed;
    }

    /// <summary>Records one observation in milliseconds.</summary>
    public void Record(LatencyStage stage, double milliseconds)
    {
        // A negative or non-finite duration is a clock artefact, not a fast cycle. Recording it
        // would drag every percentile toward a number nothing actually took.
        if (!double.IsFinite(milliseconds) || milliseconds < 0d) return;

        lock (_gate)
        {
            if (_windows.TryGetValue(stage, out Window? window)) window.Add(milliseconds);
        }
    }

    /// <summary>The distribution for every stage that has been observed.</summary>
    public IReadOnlyList<LatencySummary> Summarise()
    {
        lock (_gate)
        {
            List<LatencySummary> summaries = [];
            foreach ((LatencyStage stage, Window window) in _windows)
            {
                if (window.Count == 0) continue;
                summaries.Add(window.Summarise(stage));
            }

            return summaries;
        }
    }

    /// <summary>A fixed ring. Sized once, never grown, never reallocated.</summary>
    private sealed class Window
    {
        private readonly double[] _values = new double[WindowSize];
        private int _next;
        private int _count;

        public int Count => _count;

        public void Add(double value)
        {
            _values[_next] = value;
            _next = (_next + 1) % WindowSize;
            if (_count < WindowSize) _count++;
        }

        public LatencySummary Summarise(LatencyStage stage)
        {
            double[] sorted = new double[_count];
            Array.Copy(_values, sorted, _count);
            Array.Sort(sorted);

            return new LatencySummary(
                stage,
                _count,
                Percentile(sorted, 0.50d),
                Percentile(sorted, 0.95d),
                Percentile(sorted, 0.99d),
                sorted[^1]);
        }

        /// <summary>
        /// Nearest-rank percentile.
        ///
        /// Chosen over interpolation because an interpolated p99 invents a value between two real
        /// observations, and the point of a tail statistic is to name something that actually
        /// happened.
        /// </summary>
        private static double Percentile(double[] sorted, double quantile)
        {
            if (sorted.Length == 0) return 0d;

            int rank = (int)Math.Ceiling(quantile * sorted.Length) - 1;
            return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
        }
    }
}
