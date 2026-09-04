using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using QuantDesk.Domain.Replay;
using QuantDesk.Runtime.Time;

namespace QuantDesk.Runtime.Replay;

/// <summary>Why a replay log cannot be run.</summary>
public enum ReplayRefusal
{
    /// <summary>It can. Not a refusal.</summary>
    None,

    /// <summary>The manifest does not describe a reproducible run.</summary>
    IncompleteManifest,

    /// <summary>An envelope is missing a field the runner needs.</summary>
    MalformedEnvelope,

    /// <summary>Ingress sequence does not strictly increase.</summary>
    OutOfOrderSequence,

    /// <summary>One source's own event time moves backwards.</summary>
    NonMonotonicEventTime,

    /// <summary>Receive time moves backwards, so the replay clock cannot advance.</summary>
    NonMonotonicReceiveTime,

    /// <summary>A log with no events proves nothing about determinism.</summary>
    EmptyLog,

    /// <summary>The decision function returned nothing for an event it was given.</summary>
    UndecidedEvent,
}

/// <summary>Raised when a log cannot be replayed, naming which envelope and why.</summary>
public sealed class ReplayRefusedException(ReplayRefusal refusal, string message)
    : InvalidOperationException(message)
{
    public ReplayRefusal Refusal { get; } = refusal;
}

/// <summary>
/// Replays a recorded event log through a decision function on a clock driven by the log.
///
/// What determinism means here, and what it does not
/// -------------------------------------------------
/// The claim is narrow and worth stating exactly: **the same log, under the same manifest, produces
/// the same decisions.** Not "similar", not "within tolerance" -- the same, verified by hashing the
/// trace and comparing.
///
/// It is not a claim that the decisions were right. A replay reproduces a bug as faithfully as it
/// reproduces correct behaviour, which is the point: a decision nobody can reproduce is a decision
/// nobody can debug, and this system has already spent a day arguing about what a live run did from
/// log lines that could not be replayed.
///
/// Why the clock is driven by the log
/// ----------------------------------
/// Every deadline, every TTL, every "has this been held too long" reads a clock. If that clock is
/// the wall, a replay run in the afternoon takes different branches than the morning run it claims
/// to reproduce, and nothing about the output says so. So the runner advances a virtual clock to
/// each event's own timestamp before the decision sees it, and the decision reads that clock.
///
/// Both readings move together, and they are still separate readings. Section 8.2 keeps wall time
/// and monotonic time apart because they answer different questions -- what time is it, versus how
/// long has it been -- and this system has already been bitten by conflating them: uptime came back
/// negative because a wall clock is not monotonic, and then again because a static initialiser ran
/// after the timestamp it was supposed to precede. Under replay both are functions of the log,
/// which is what makes a TTL expire at the same event every time.
///
/// What it refuses, and why each refusal is not pedantry
/// ----------------------------------------------------
/// A log whose ingress sequence does not strictly increase, because the order *is* the input --
/// "deterministic given these events" means nothing if their order is negotiable. A log whose event
/// time moves backwards, which is the wall clock's non-monotonicity leaking into a recording and
/// would make a TTL expire before it started. An empty log, which reproduces trivially and proves
/// nothing. Each is a condition under which the word "deterministic" would still be printed and
/// would no longer be true.
/// </summary>
public sealed class ReplayRunner
{
    /// <summary>Nanoseconds in one <see cref="TimeSpan"/> tick.</summary>
    private const long NanosecondsPerTick = 100L;

    /// <summary>
    /// Replays the log and returns what happened, or refuses with the reason.
    /// </summary>
    /// <param name="manifest">
    /// What produced the decisions: configuration, model artifacts, policy version, seed. Folded
    /// into the trace hash, so replaying the same events under a different policy is visibly a
    /// different run rather than a silent one.
    /// </param>
    /// <param name="log">The recorded events, in ingress order.</param>
    /// <param name="decide">
    /// The decision under test, given the clock and one event. Returns the decision code and the
    /// payload that determines it -- both are hashed, so a decision that reaches the same verdict
    /// by different reasoning still shows as a divergence.
    /// </param>
    public ReplayRunResult Run(
        ReplayManifest manifest,
        IReadOnlyList<ReplayEnvelope> log,
        Func<IRuntimeClock, ReplayEnvelope, (string Code, string Payload)?> decide)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(decide);

        Validate(manifest, log);

        // Seeded from when the runtime saw the log's first event rather than from now, so a run
        // started at any moment sees the times the recording saw.
        //
        // The monotonic receive timeline, not venue event time and not the wall clock.
        //
        // Venue time is per-instrument: a feed carrying several
        // symbols carries several venue clocks, and their stamps interleave -- a live recording of
        // six crypto order books opens with jumps of -4s and -12s between consecutive events purely
        // because each book was last touched at a different moment. Advancing the clock along that
        // would run the replay backwards. Receive time is one process's own clock read in ingress
        // order, so it is monotonic by construction, and it is also the moment a live decision was
        // actually made -- which is the thing a replay has to reproduce.
        var clock = new VirtualRuntimeClock(UnixNanosecondsToUtc(log[0].ReceiveUnixNanoseconds));
        long previousReceiveNanoseconds = log[0].ReceiveMonotonicNanoseconds;

        var decisions = new List<ReplayDecision>(log.Count);
        var trace = new StringBuilder();
        AppendManifest(trace, manifest);

        foreach (ReplayEnvelope envelope in log)
        {
            // Time moves before the decision sees the event, never after. A decision that reads the
            // clock is then reading the moment its own event arrived, which is what the live path
            // does and what a replay has to imitate for a deadline to fall the same way.
            clock.Advance(
                TimeSpan.FromTicks(
                    (envelope.ReceiveMonotonicNanoseconds - previousReceiveNanoseconds) / NanosecondsPerTick));
            previousReceiveNanoseconds = envelope.ReceiveMonotonicNanoseconds;

            (string Code, string Payload)? outcome = decide(clock, envelope);
            if (outcome is not { } decided)
            {
                throw new ReplayRefusedException(
                    ReplayRefusal.UndecidedEvent,
                    $"Event {envelope.IngressSequence} produced no decision. A replay that skips an "
                    + "event silently is a replay of a different log.");
            }

            string payloadHash = Hash(decided.Payload);
            var decision = new ReplayDecision(envelope.IngressSequence, decided.Code, payloadHash);
            decisions.Add(decision);
            AppendDecision(trace, envelope, decision);
        }

        return new ReplayRunResult(
            manifest.EvidenceClass,
            log.Count,
            Hash(trace.ToString()),
            decisions,
            clock.UtcNow,
            clock.MonotonicTimestamp);
    }

    /// <summary>
    /// Runs the same log twice and requires the two traces to agree.
    ///
    /// The check a single run cannot make. One pass produces a hash; only a second pass says whether
    /// that hash was a property of the log or an accident of this execution. Unseeded randomness,
    /// dictionary enumeration order, ambient static state and anything reading the wall clock all
    /// survive a single run and fail here -- which is why the release gate is this method rather
    /// than the existence of a replay facility.
    ///
    /// The parameter is a factory so each pass gets its own decider. A stateful decider is the
    /// normal case rather than the exception -- replaying a decision means replaying what it
    /// accumulated -- and sharing one between the passes would compare a cold run against a warm
    /// one and call the difference non-determinism.
    /// </summary>
    public ReplayRunResult RunAndProveDeterministic(
        ReplayManifest manifest,
        IReadOnlyList<ReplayEnvelope> log,
        Func<Func<IRuntimeClock, ReplayEnvelope, (string Code, string Payload)?>> decider)
    {
        ArgumentNullException.ThrowIfNull(decider);

        // A factory, not a decider. The first version took one delegate and called it twice, which
        // is unsound for exactly the deciders this exists to check: anything that accumulates state
        // -- a market-state machine, a position, a warmed recursion -- carries the first pass's
        // state into the second and then disagrees with itself. Connecting the real market-state
        // replay produced that divergence immediately, and it was the runner's fault rather than
        // the system's.
        //
        // Taking a factory makes the fresh start structural: there is no way to hand this a decider
        // that both passes share.
        ReplayRunResult first = Run(manifest, log, decider());
        ReplayRunResult second = Run(manifest, log, decider());

        if (!string.Equals(
                first.DeterministicTraceHash, second.DeterministicTraceHash, StringComparison.Ordinal))
        {
            throw new ReplayRefusedException(
                ReplayRefusal.None,
                $"The same log produced two traces: {first.DeterministicTraceHash} then "
                + $"{second.DeterministicTraceHash}. Something outside the log decided part of the "
                + $"outcome. First divergence at {FirstDivergence(first, second)}.");
        }

        return first;
    }

    /// <summary>Where two runs of the same log first disagreed, for the message that reports it.</summary>
    private static string FirstDivergence(ReplayRunResult first, ReplayRunResult second)
    {
        int shared = Math.Min(first.Decisions.Count, second.Decisions.Count);
        for (int index = 0; index < shared; index++)
        {
            ReplayDecision left = first.Decisions[index];
            ReplayDecision right = second.Decisions[index];
            if (!string.Equals(left.DecisionCode, right.DecisionCode, StringComparison.Ordinal)
                || !string.Equals(
                    left.DeterministicPayloadHash, right.DeterministicPayloadHash, StringComparison.Ordinal))
            {
                return $"event {left.IngressSequence}: {left.DecisionCode} then {right.DecisionCode}";
            }
        }

        return first.Decisions.Count == second.Decisions.Count
            ? "no decision, but the traces differ -- the manifest or the event count moved"
            : $"decision count: {first.Decisions.Count} then {second.Decisions.Count}";
    }

    private static void Validate(ReplayManifest manifest, IReadOnlyList<ReplayEnvelope> log)
    {
        if (!manifest.IsValid())
        {
            throw new ReplayRefusedException(
                ReplayRefusal.IncompleteManifest,
                "A replay without the configuration, artifacts, policy version and seed behind it "
                + "cannot say what it reproduced.");
        }

        if (log.Count == 0)
        {
            throw new ReplayRefusedException(
                ReplayRefusal.EmptyLog,
                "An empty log reproduces itself trivially and establishes nothing.");
        }

        long previousSequence = 0;
        long previousReceiveNanoseconds = long.MinValue;
        var previousEventPerSource = new Dictionary<string, long>(StringComparer.Ordinal);

        // A log written before the monotonic timeline existed carries zero on every envelope, which
        // would read as a session where no time passed at all -- every deadline expiring together
        // on the first event. Refusing it is the honest outcome: it was recorded against a wall
        // clock that could step, which is the thing that made this field necessary.
        if (log.Count > 1 && log.All(envelope => envelope.ReceiveMonotonicNanoseconds == 0L))
        {
            throw new ReplayRefusedException(
                ReplayRefusal.MalformedEnvelope,
                "The log carries no monotonic receive timeline, so a replay cannot advance time "
                + "through it. It was recorded before that field existed.");
        }

        foreach (ReplayEnvelope envelope in log)
        {
            if (envelope is null || !envelope.IsValid())
            {
                throw new ReplayRefusedException(
                    ReplayRefusal.MalformedEnvelope,
                    $"Envelope at sequence {envelope?.IngressSequence.ToString(CultureInfo.InvariantCulture) ?? "?"} "
                    + "is missing a field the runner needs.");
            }

            // The order is the input. A log that does not fix it cannot fix the outcome.
            if (envelope.IngressSequence <= previousSequence)
            {
                throw new ReplayRefusedException(
                    ReplayRefusal.OutOfOrderSequence,
                    $"Ingress sequence {envelope.IngressSequence} does not follow {previousSequence}. "
                    + "Determinism given a set of events means nothing if their order is negotiable.");
            }

            // The timeline the replay clock advances along. Non-monotonicity here is the runtime's
            // own clock stepping backwards, which is a real defect: a TTL replayed across it would
            // expire before it started, and this system has already seen uptime come back negative
            // from exactly this.
            if (envelope.ReceiveMonotonicNanoseconds < previousReceiveNanoseconds)
            {
                throw new ReplayRefusedException(
                    ReplayRefusal.NonMonotonicReceiveTime,
                    $"Receive time moves backwards at sequence {envelope.IngressSequence}. The "
                    + "runtime's own clock went backwards, so the replay cannot be advanced forwards.");
            }

            // Venue time is checked per source, not globally. Across a multi-instrument feed each
            // symbol carries its own venue clock and their stamps interleave, so a global check
            // refuses ordinary recordings -- it refused this system's first real one. Within a
            // single source it still has to advance: one order book whose stamps go backwards is a
            // feed defect, and a staleness test computed against it would read the wrong sign.
            if (previousEventPerSource.TryGetValue(envelope.Source, out long previousForSource)
                && envelope.EventUnixNanoseconds < previousForSource)
            {
                throw new ReplayRefusedException(
                    ReplayRefusal.NonMonotonicEventTime,
                    $"Event time for source {envelope.Source} moves backwards at sequence "
                    + $"{envelope.IngressSequence}. One source's own stamps cannot run backwards.");
            }

            previousSequence = envelope.IngressSequence;
            previousReceiveNanoseconds = envelope.ReceiveMonotonicNanoseconds;
            previousEventPerSource[envelope.Source] = envelope.EventUnixNanoseconds;
        }
    }
    /// <summary>
    /// The manifest, first in the trace.
    ///
    /// Included so replaying the same events under a different policy version, a different set of
    /// model artifacts or a different seed produces a visibly different hash. Without it a replay
    /// could "reproduce" a result that came from code which no longer exists.
    /// </summary>
    private static void AppendManifest(StringBuilder trace, ReplayManifest manifest)
    {
        Field(trace, "manifest");
        Field(trace, manifest.ConfigurationHash);
        Field(trace, manifest.ModelArtifactsHash);
        Field(trace, manifest.PolicyVersion);
        Field(trace, manifest.RandomSeed.ToString(CultureInfo.InvariantCulture));
        Field(trace, manifest.EvidenceClass.ToString());
    }

    private static void AppendDecision(
        StringBuilder trace, ReplayEnvelope envelope, ReplayDecision decision)
    {
        Field(trace, envelope.IngressSequence.ToString(CultureInfo.InvariantCulture));
        Field(trace, envelope.EventType);
        Field(trace, envelope.EventUnixNanoseconds.ToString(CultureInfo.InvariantCulture));
        Field(trace, decision.DecisionCode);
        Field(trace, decision.DeterministicPayloadHash);
    }

    /// <summary>
    /// Appends one field, length-prefixed.
    ///
    /// Length-prefixed rather than delimited because a delimiter has to be a character the values
    /// cannot contain, and decision codes and event types are free-form strings chosen by whoever
    /// writes the next expert. Prefixing makes the split unambiguous whatever they choose, so two
    /// different traces cannot serialise to the same string and collide in the hash -- which would
    /// be a false "deterministic" produced by naming something badly.
    /// </summary>
    private static void Field(StringBuilder trace, string value) =>
        trace.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);


    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static DateTimeOffset UnixNanosecondsToUtc(long nanoseconds) =>
        DateTimeOffset.UnixEpoch.AddTicks(nanoseconds / NanosecondsPerTick);
}
