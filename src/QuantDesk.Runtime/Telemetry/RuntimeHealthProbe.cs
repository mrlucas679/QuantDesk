using System.Diagnostics;

namespace QuantDesk.Runtime.Telemetry;

/// <param name="ManagedHeapBytes">Bytes the CLR believes are live.</param>
/// <param name="WorkingSetBytes">Bytes the operating system has given the process.</param>
/// <param name="Gen0Collections">Cheap collections since start.</param>
/// <param name="Gen1Collections">Intermediate collections since start.</param>
/// <param name="Gen2Collections">Full collections since start.</param>
/// <param name="TotalPauseMilliseconds">Time spent stopped for garbage collection.</param>
/// <param name="ThreadPoolThreads">Threads the pool currently holds.</param>
/// <param name="PendingWorkItems">Work queued and not yet started.</param>
/// <param name="UptimeSeconds">How long the process has been running.</param>
public readonly record struct RuntimeHealth(
    long ManagedHeapBytes,
    long WorkingSetBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    double TotalPauseMilliseconds,
    int ThreadPoolThreads,
    long PendingWorkItems,
    double UptimeSeconds);

/// <summary>
/// Reads what the runtime is doing to itself, which is the half of section 24 that latency does
/// not cover.
///
/// What these numbers are for
/// --------------------------
/// Not performance tuning. Section 30.3 makes "memory plateaus under soak after warm-up" a release
/// gate, and the only way to know whether it plateaus is to watch it. A managed heap that climbs
/// steadily across a day is a leak whatever the latency looks like, and the shape that produces it
/// -- a collection that grows with every cycle -- is the exact defect the constitution's ban on
/// unbounded collections exists to prevent. This system has already produced one: a shadow ledger
/// that rewrote an 83 KB file every two to four seconds and would have reached eight megabytes.
///
/// Gen 2 counts matter more than the heap size. A process can hold a large heap quite happily; one
/// that is collecting gen 2 repeatedly is spending its time on memory rather than on decisions, and
/// each of those collections is a pause during which a quote goes stale.
///
/// Pending work items are the queue-depth signal the constitution asks for. A number that grows is
/// a lane falling behind its own cadence, which shows up as staleness long before it shows up as
/// an error.
///
/// Every reading here is a counter the runtime already maintains. Nothing is sampled on a timer and
/// nothing is computed on the hot path.
/// </summary>
public static class RuntimeHealthProbe
{
    /// <summary>
    /// Process start, on the monotonic clock.
    ///
    /// Wall-clock time is not monotonic. Two consecutive reads of <c>DateTimeOffset.UtcNow</c> can
    /// decrease -- NTP steps it, and its own resolution is coarse enough that the tick can appear
    /// to run backwards. Measured here: an uptime of -8.77e-05 seconds, from a subtraction that
    /// cannot be negative if the clock only moves forward.
    ///
    /// Section 8.2 states the rule this broke: UTC time is for causality and audit, monotonic time
    /// is for latency and deadline calculations. A duration computed from wall clock is the same
    /// class of error as a deadline computed from it, and the fact that it produced a visibly
    /// impossible number is the only reason it was caught rather than quietly skewing a percentile.
    ///
    /// Switching to the monotonic clock was necessary and not sufficient: the reading stayed
    /// negative. Without an explicit static constructor the CLR marks the type beforefieldinit and
    /// may defer this initialiser until the field is first *read* -- which, in an argument list that
    /// calls GetTimestamp() before reading the field, happens afterwards. The start was therefore
    /// being stamped later than the "now" it was subtracted from. The static constructor below
    /// removes beforefieldinit so initialisation is ordered, and Read takes the two timestamps in
    /// an order that does not depend on that guarantee anyway.
    /// </summary>
    private static readonly long StartedTimestamp;

    static RuntimeHealthProbe() => StartedTimestamp = Stopwatch.GetTimestamp();

    public static RuntimeHealth Read()
    {
        // Without forcing a collection. Asking the GC to collect in order to measure it would make
        // the measurement the largest thing the measurement observes, and the constitution forbids
        // GC.Collect in normal operation for the same reason.
        // Start read before now, so the subtraction cannot be negative whatever the runtime does
        // about initialisation order.
        long started = StartedTimestamp;
        long now = Stopwatch.GetTimestamp();

        long managed = GC.GetTotalMemory(forceFullCollection: false);

        long workingSet;
        try
        {
            workingSet = Process.GetCurrentProcess().WorkingSet64;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or NotSupportedException or PlatformNotSupportedException)
        {
            // A container that will not report its own process is a gap in the reading, not a
            // reason to fail the probe that the rest of the health surface depends on.
            workingSet = 0L;
        }

        return new RuntimeHealth(
            managed,
            workingSet,
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2),
            GC.GetTotalPauseDuration().TotalMilliseconds,
            ThreadPool.ThreadCount,
            ThreadPool.PendingWorkItemCount,
            (now - started) / (double)Stopwatch.Frequency);
    }
}
