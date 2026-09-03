using QuantDesk.Domain.Replay;
using QuantDesk.Runtime.Replay;
using QuantDesk.Runtime.Time;

namespace QuantDesk.Runtime.Tests.Replay;

/// <summary>
/// Whether a recorded session actually replays to the same decisions.
///
/// Section 22 makes deterministic replay a release gate, and the gate is not "a replay facility
/// exists". It is that running the same log twice produces the same trace -- because a single run
/// always produces a hash, and only a second run says whether that hash was a property of the log
/// or an accident of the execution.
///
/// The tests below are mostly about the ways it stops being true: a decision that reads the wall
/// clock, a log whose order is negotiable, a manifest that changed underneath. Each of those leaves
/// a system that still prints "replayed successfully".
/// </summary>
public sealed class ReplayRunnerTests
{
    private static readonly DateTimeOffset SessionStart =
        new(2026, 9, 3, 13, 30, 0, TimeSpan.Zero);

    // ------------------------------------------------------------------ the property being claimed

    [Fact]
    public void TheSameLogProducesTheSameTraceTwice()
    {
        IReadOnlyList<ReplayEnvelope> log = Log(events: 40);

        ReplayRunResult result = new ReplayRunner()
            .RunAndProveDeterministic(Manifest(), log, HoldForFiveMinutes);

        Assert.Equal(40, result.EventCount);
        Assert.NotEmpty(result.DeterministicTraceHash);
    }

    [Fact]
    public void ADecisionThatReadsTheInjectedClockReplaysIdentically()
    {
        // The whole reason the clock is injected. This decider exits a position five minutes after
        // entering it, which is a deadline -- exactly the kind of logic that silently depends on
        // when the process happens to be running.
        IReadOnlyList<ReplayEnvelope> log = Log(events: 40);
        var runner = new ReplayRunner();

        ReplayRunResult first = runner.Run(Manifest(), log, HoldForFiveMinutes);
        ReplayRunResult second = runner.Run(Manifest(), log, HoldForFiveMinutes);

        Assert.Equal(first.DeterministicTraceHash, second.DeterministicTraceHash);

        // And it exits on the same event both times, not merely produces the same hash.
        Assert.Equal(
            first.Decisions.First(decision => decision.DecisionCode == "EXIT").IngressSequence,
            second.Decisions.First(decision => decision.DecisionCode == "EXIT").IngressSequence);
    }

    [Fact]
    public void ADeadlineFallsOnTheEventItsOwnTimestampImplies()
    {
        // Events are one minute apart, so a five-minute hold entered on the first event must end on
        // the sixth. If the clock were the wall rather than the log, this would depend on how long
        // the test took to run.
        IReadOnlyList<ReplayEnvelope> log = Log(events: 10);

        ReplayRunResult result = new ReplayRunner().Run(Manifest(), log, HoldForFiveMinutes);

        ReplayDecision exit = result.Decisions.First(decision => decision.DecisionCode == "EXIT");
        Assert.Equal(6, exit.IngressSequence);
    }

    [Fact]
    public void WallTimeAndMonotonicTimeAreBothDrivenByTheLog()
    {
        // Section 8.2 keeps them apart because they answer different questions. Under replay both
        // are functions of the log, which is what makes a TTL expire at the same event every time.
        IReadOnlyList<ReplayEnvelope> log = Log(events: 10);

        ReplayRunResult result = new ReplayRunner().Run(Manifest(), log, AlwaysHold);

        Assert.Equal(SessionStart.AddMinutes(9), result.FinalVirtualTime);
        Assert.Equal(TimeSpan.FromMinutes(9).Ticks, result.FinalMonotonicTimestamp);
    }

    // ----------------------------------------------------------- what the second run actually finds

    [Fact]
    public void ADecisionWithUnseededRandomnessIsCaught()
    {
        // The reason RunAndProveDeterministic exists. A single run of this returns a perfectly
        // respectable hash and reveals nothing.
        IReadOnlyList<ReplayEnvelope> log = Log(events: 20);

        ReplayRefusedException refused = Assert.Throws<ReplayRefusedException>(() =>
            new ReplayRunner().RunAndProveDeterministic(
                Manifest(), log, (_, _) => ("HOLD", Random.Shared.Next().ToString())));

        Assert.Contains("Something outside the log decided part of the outcome", refused.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ADecisionThatReadsTheWallClockRatherThanTheInjectedOneIsCaught()
    {
        // The failure this whole design is arranged against. Nothing stops a decider calling
        // DateTimeOffset.UtcNow -- the type system certainly does not -- so the gate has to be that
        // two runs disagree, and they do, because the wall moved between them.
        IReadOnlyList<ReplayEnvelope> log = Log(events: 5);

        Assert.Throws<ReplayRefusedException>(() =>
            new ReplayRunner().RunAndProveDeterministic(
                Manifest(),
                log,
                (_, _) => ("HOLD", DateTimeOffset.UtcNow.Ticks.ToString())));
    }

    [Fact]
    public void ADivergenceNamesTheEventItHappenedOn()
    {
        // A hash mismatch with no location is a bug report saying "it differs".
        int calls = 0;
        IReadOnlyList<ReplayEnvelope> log = Log(events: 6);

        ReplayRefusedException refused = Assert.Throws<ReplayRefusedException>(() =>
            new ReplayRunner().RunAndProveDeterministic(
                Manifest(),
                log,
                (_, envelope) =>
                {
                    calls++;
                    // Diverges only on the fourth event of the second pass.
                    bool secondPass = calls > log.Count;
                    return envelope.IngressSequence == 4 && secondPass
                        ? ("EXIT", "different")
                        : ("HOLD", "same");
                }));

        Assert.Contains("event 4", refused.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------ the refusals

    [Fact]
    public void ALogWhoseOrderIsNegotiableIsRefused()
    {
        // "Deterministic given these events" means nothing if their order is not fixed.
        List<ReplayEnvelope> log = [.. Log(events: 5)];
        (log[2], log[3]) = (log[3], log[2]);

        ReplayRefusedException refused = Assert.Throws<ReplayRefusedException>(() =>
            new ReplayRunner().Run(Manifest(), log, AlwaysHold));

        Assert.Equal(ReplayRefusal.OutOfOrderSequence, refused.Refusal);
    }

    [Fact]
    public void ALogWhoseClockWentBackwardsIsRefused()
    {
        // Wall-clock non-monotonicity leaking into a recording. This system has already had uptime
        // come back negative from exactly this, and a TTL replayed across it would expire before it
        // started.
        List<ReplayEnvelope> log = [.. Log(events: 5)];
        log[3] = log[3] with { EventUnixNanoseconds = log[2].EventUnixNanoseconds - 1_000_000L };

        ReplayRefusedException refused = Assert.Throws<ReplayRefusedException>(() =>
            new ReplayRunner().Run(Manifest(), log, AlwaysHold));

        Assert.Equal(ReplayRefusal.NonMonotonicEventTime, refused.Refusal);
    }

    [Fact]
    public void AnEmptyLogIsRefusedRatherThanReportedAsReproduced()
    {
        ReplayRefusedException refused = Assert.Throws<ReplayRefusedException>(() =>
            new ReplayRunner().Run(Manifest(), [], AlwaysHold));

        Assert.Equal(ReplayRefusal.EmptyLog, refused.Refusal);
    }

    [Fact]
    public void AManifestThatCannotSayWhatItReproducedIsRefused()
    {
        ReplayRefusedException refused = Assert.Throws<ReplayRefusedException>(() =>
            new ReplayRunner().Run(
                Manifest() with { PolicyVersion = "  " }, Log(events: 3), AlwaysHold));

        Assert.Equal(ReplayRefusal.IncompleteManifest, refused.Refusal);
    }

    [Fact]
    public void AnEventThatProducedNoDecisionIsRefused()
    {
        // A replay that quietly skips an event is a replay of a different log.
        ReplayRefusedException refused = Assert.Throws<ReplayRefusedException>(() =>
            new ReplayRunner().Run(
                Manifest(),
                Log(events: 4),
                (_, envelope) => envelope.IngressSequence == 3 ? null : ("HOLD", "x")));

        Assert.Equal(ReplayRefusal.UndecidedEvent, refused.Refusal);
    }

    // ------------------------------------------------------------------------ the manifest matters

    [Fact]
    public void TheSameEventsUnderADifferentPolicyAreVisiblyADifferentRun()
    {
        // Otherwise a replay could "reproduce" a result that came from code which no longer exists.
        IReadOnlyList<ReplayEnvelope> log = Log(events: 10);
        var runner = new ReplayRunner();

        string original = runner.Run(Manifest(), log, HoldForFiveMinutes).DeterministicTraceHash;
        string repolicied = runner
            .Run(Manifest() with { PolicyVersion = "policy-v2" }, log, HoldForFiveMinutes)
            .DeterministicTraceHash;

        Assert.NotEqual(original, repolicied);
    }

    [Fact]
    public void TheSameEventsUnderDifferentModelArtifactsAreVisiblyADifferentRun()
    {
        IReadOnlyList<ReplayEnvelope> log = Log(events: 10);
        var runner = new ReplayRunner();

        string original = runner.Run(Manifest(), log, HoldForFiveMinutes).DeterministicTraceHash;
        string refitted = runner
            .Run(Manifest() with { ModelArtifactsHash = "artifacts-after-a-retrain" },
                 log, HoldForFiveMinutes)
            .DeterministicTraceHash;

        Assert.NotEqual(original, refitted);
    }

    [Fact]
    public void TwoTracesThatSplitTheirFieldsDifferentlyDoNotCollide()
    {
        // Fields are length-prefixed rather than delimited, because a decision code containing the
        // delimiter would otherwise let one trace serialise to another's string -- a hash collision
        // engineered by naming something badly.
        IReadOnlyList<ReplayEnvelope> log = Log(events: 2);
        var runner = new ReplayRunner();

        string first = runner
            .Run(Manifest(), log, (_, _) => ("HOLD_LONG", "x")).DeterministicTraceHash;
        string second = runner
            .Run(Manifest(), log, (_, _) => ("HOLD", "LONGx")).DeterministicTraceHash;

        Assert.NotEqual(first, second);
    }

    // -------------------------------------------------------------------- deciders under test

    /// <summary>Enters on the first event and exits five minutes of virtual time later.</summary>
    private static (string Code, string Payload)? HoldForFiveMinutes(
        IRuntimeClock clock, ReplayEnvelope envelope)
    {
        if (envelope.IngressSequence == 1) return ("ENTER", clock.UtcNow.ToString("O"));

        bool expired = clock.UtcNow - SessionStart >= TimeSpan.FromMinutes(5);
        return expired ? ("EXIT", clock.UtcNow.ToString("O")) : ("HOLD", clock.UtcNow.ToString("O"));
    }

    private static (string Code, string Payload)? AlwaysHold(
        IRuntimeClock clock, ReplayEnvelope envelope) =>
        ("HOLD", envelope.IngressSequence.ToString());

    // ------------------------------------------------------------------------------- fixtures

    private static ReplayManifest Manifest() => new(
        ConfigurationHash: "config-abc",
        ModelArtifactsHash: "artifacts-abc",
        PolicyVersion: "policy-v1",
        RandomSeed: 20260903,
        EvidenceClass: ReplayEvidenceClass.PassiveHistoricalReplay);

    /// <summary>A log of evenly spaced events, one minute apart.</summary>
    private static IReadOnlyList<ReplayEnvelope> Log(int events)
    {
        long start = (SessionStart.UtcDateTime - DateTime.UnixEpoch).Ticks * 100L;
        return
        [
            .. Enumerable.Range(0, events).Select(index => new ReplayEnvelope(
                SchemaVersion: 1,
                IngressSequence: index + 1,
                Source: "crypto-quotes",
                EventUnixNanoseconds: start + (index * 60_000_000_000L),
                ReceiveOffsetNanoseconds: 1_500_000L,
                EventType: "quote",
                Payload: [(byte)(index % 256)])),
        ];
    }
}
