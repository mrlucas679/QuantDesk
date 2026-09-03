namespace QuantDesk.Runtime.Time;

/// <summary>
/// The two clocks the runtime reads, and the conversion between a duration and the monotonic one.
///
/// Wall time and monotonic time are separate readings because they answer separate questions --
/// what time is it, versus how long has it been -- and section 8.2 keeps them apart for a reason
/// this system has met twice: uptime came back negative because a wall clock is not monotonic, and
/// then again because a static initialiser ran after the timestamp meant to precede it.
///
/// Why the conversion belongs here
/// -------------------------------
/// A monotonic timestamp has no fixed scale. It means whatever the clock that produced it means,
/// and the two implementations disagree: the live clock counts in Stopwatch ticks, the virtual one
/// in TimeSpan ticks. On Linux those differ by a factor of a hundred -- Stopwatch.Frequency is
/// 1,000,000,000 against TimeSpan's 10,000,000 -- and on Windows they happen to coincide, which is
/// worse, because it means the mistake passes on a developer's machine and changes behaviour in
/// the container.
///
/// Nine places converted a duration with a bare <c>Stopwatch.Frequency</c>, including the exit
/// engine's maximum holding period and both strategy compilers' candidate lifetimes. Under a
/// virtual clock every one of them was out by that factor of a hundred: a five-minute hold became
/// five hundred minutes of virtual time, so a replayed session would never have exited on time and
/// no test using the virtual clock was measuring what it claimed to.
///
/// Putting the conversion on the clock removes the class of error rather than the instances. There
/// is no correct way to turn a duration into monotonic ticks without asking the clock that will be
/// compared against, so asking it is the only thing left to do.
/// </summary>
public interface IRuntimeClock
{
    /// <summary>What time it is. Not monotonic; may step backwards.</summary>
    DateTimeOffset UtcNow { get; }

    /// <summary>A reading that only moves forward, in this clock's own units.</summary>
    long MonotonicTimestamp { get; }

    /// <summary>How long passed between two monotonic readings from this clock.</summary>
    double ElapsedMilliseconds(long start, long end);

    /// <summary>
    /// A duration expressed in this clock's monotonic units, so it can be added to a timestamp.
    ///
    /// Saturates rather than overflowing: a deadline that wrapped to a negative number would read
    /// as already expired, which turns an implausibly long TTL into an immediate exit.
    /// </summary>
    long MonotonicTicksFor(TimeSpan duration);
}
