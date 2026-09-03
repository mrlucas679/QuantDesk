using Microsoft.Extensions.Logging.Abstractions;
using QuantDesk.Api.PaperTrading;
using QuantDesk.Domain.Market;
using QuantDesk.Runtime.Replay;
using QuantDesk.Runtime.Time;

namespace QuantDesk.Api.Tests;

/// <summary>
/// The end of section 22's gate: a recorded session replayed through real decision code.
///
/// What was missing until now
/// --------------------------
/// The runner and the recorder both existed and were both disconnected. Recording was wired one
/// commit ago; nothing replayed what it wrote, so determinism was still only ever demonstrated by
/// tests against deciders written for the runner's own tests.
///
/// These record a session the way the market-data path records one, then replay it through
/// <c>MarketStateStore</c> -- the same state machine the live pipeline calls before every entry,
/// and whose refusal it reports as StaleMarketData. Two passes must agree.
///
/// What is still not covered, and the tests do not pretend otherwise: strategy selection needs
/// bars, the cost gate a fee schedule, the risk governor a portfolio. None are in a market-data
/// log.
/// </summary>
public sealed class SessionReplayServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"quantdesk-replay-svc-{Guid.NewGuid():N}");

    private readonly string? _previousRoot =
        Environment.GetEnvironmentVariable("QUANTDESK_REPLAY_LOG_ROOT");

    public SessionReplayServiceTests()
    {
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("QUANTDESK_REPLAY_LOG_ROOT", _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("QUANTDESK_REPLAY_LOG_ROOT", _previousRoot);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void ARecordedSessionReplaysThroughTheRealMarketStateMachine()
    {
        // The claim the gate rests on, made against events a session produced and code the live
        // pipeline calls -- not against a decider written for this test.
        RecordSession(SessionStart, events: 80);
        var state = new SessionReplayState();

        Service(state).Replay();

        SessionReplaySnapshot snapshot = state.Snapshot();
        Assert.Equal("replayed", snapshot.State);
        Assert.Equal(80, snapshot.EventCount);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.TraceHash));
        Assert.Null(snapshot.Reason);
    }

    [Fact]
    public void TheSameSessionProducesTheSameTraceAcrossSeparateRuns()
    {
        // Not merely twice inside one runner call -- twice from separate service instances, which
        // is the shape a restart actually takes.
        RecordSession(SessionStart, events: 40);

        var first = new SessionReplayState();
        var second = new SessionReplayState();
        Service(first).Replay();
        Service(second).Replay();

        Assert.Equal(first.Snapshot().TraceHash, second.Snapshot().TraceHash);
        Assert.Equal("replayed", second.Snapshot().State);
    }

    [Fact]
    public void ADifferentSessionProducesADifferentTrace()
    {
        // A hash that did not depend on the events would agree with itself forever and prove
        // nothing about the session it claims to describe.
        RecordSession(SessionStart, events: 40);
        var quiet = new SessionReplayState();
        Service(quiet).Replay();

        Directory.Delete(_root, recursive: true);
        Directory.CreateDirectory(_root);
        RecordSession(SessionStart, events: 40, spreadWidening: 12d);
        var wide = new SessionReplayState();
        Service(wide).Replay();

        Assert.NotEqual(quiet.Snapshot().TraceHash, wide.Snapshot().TraceHash);
    }

    [Fact]
    public void TheSessionBeingWrittenNowIsSkipped()
    {
        // Replaying a file while it grows compares one prefix against another and reports a
        // divergence that is an artefact of when each pass happened to read.
        RecordSession(SessionStart, events: 20);

        var clock = new VirtualRuntimeClock(SessionStart.AddHours(1));
        using var current = new MarketDataSessionRecorder(clock, NullLogger.Instance, _root);
        current.Record(Quote(0, clock));

        var state = new SessionReplayState();
        new SessionReplayService(current, state, new LiveRuntimeClock(), NullLogger<SessionReplayService>.Instance).Replay();

        Assert.Equal("replayed", state.Snapshot().State);
        Assert.Equal(20, state.Snapshot().EventCount);
        Assert.NotEqual(Path.GetFileName(current.Path), state.Snapshot().SessionFile);
    }

    [Fact]
    public void NoCompletedSessionIsReportedRatherThanTreatedAsSuccess()
    {
        // A fresh deployment has replayed nothing, and that must not read as a passing gate.
        var state = new SessionReplayState();

        Service(state).Replay();

        Assert.Equal("unavailable", state.Snapshot().State);
        Assert.Equal("NO_COMPLETED_SESSION", state.Snapshot().Reason);
    }

    [Fact]
    public void ACorruptedSessionIsReportedRatherThanCrashingTheHost()
    {
        File.WriteAllText(Path.Combine(_root, "session-20260101-000000.jsonl"), "{ not json");
        var state = new SessionReplayState();

        Service(state).Replay();

        Assert.Equal("unavailable", state.Snapshot().State);
        Assert.Equal("SESSION_UNREADABLE", state.Snapshot().Reason);
    }

    [Fact]
    public void ALogWhoseOrderWasTamperedWithIsRefused()
    {
        // The runner refuses a log whose ingress sequence does not strictly increase, because the
        // order is the input. This proves that refusal reaches the status surface rather than
        // being swallowed.
        RecordSession(SessionStart, events: 12);
        string session = Directory.GetFiles(_root, "session-*.jsonl").Single();
        string[] lines = File.ReadAllLines(session);
        (lines[3], lines[4]) = (lines[4], lines[3]);
        File.WriteAllLines(session, lines);

        var state = new SessionReplayState();
        Service(state).Replay();

        Assert.Equal("refused", state.Snapshot().State);
        Assert.Contains("Ingress sequence", state.Snapshot().Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStateMachineIsFreshForEachPass()
    {
        // A store shared between the two passes would carry the first pass's version numbers into
        // the second and agree with itself for the wrong reason.
        var first = MarketStateReplay.Decider();
        var second = MarketStateReplay.Decider();
        var clock = new VirtualRuntimeClock(SessionStart);

        var log = RecordSession(SessionStart, events: 5);

        Assert.Equal(
            log.Select(entry => first(clock, entry)!.Value.Payload),
            log.Select(entry => second(clock, entry)!.Value.Payload));
    }

    // ------------------------------------------------------------------------------- fixtures

    private static readonly DateTimeOffset SessionStart =
        new(2026, 9, 3, 8, 0, 0, TimeSpan.Zero);

    private SessionReplayService Service(SessionReplayState state)
    {
        // A recorder whose own file is absent, so nothing is skipped as "currently being written".
        var idle = new MarketDataSessionRecorder(
            new VirtualRuntimeClock(SessionStart.AddYears(1)), NullLogger.Instance, _root);
        idle.Dispose();
        File.Delete(idle.Path!);

        return new SessionReplayService(
            idle, state, new LiveRuntimeClock(), NullLogger<SessionReplayService>.Instance);
    }

    private IReadOnlyList<Domain.Replay.ReplayEnvelope> RecordSession(
        DateTimeOffset start, int events, double spreadWidening = 1d)
    {
        var clock = new VirtualRuntimeClock(start);
        using var recorder = new MarketDataSessionRecorder(clock, NullLogger.Instance, _root);

        for (int index = 0; index < events; index++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            recorder.Record(Quote(index, clock, spreadWidening));
        }

        recorder.Dispose();
        return ReplayEventRecorder.ReadFile(recorder.Path!);
    }

    private static NormalizedMarketEvent Quote(
        int index, IRuntimeClock clock, double spreadWidening = 1d) =>
        NormalizedMarketEvent.FromQuote(new QuoteEvent(
            EventId: index + 1,
            InstrumentSlot: 0,
            Bid: 30_000d + index,
            Ask: 30_000d + index + (2d * spreadWidening),
            BidSize: 1d,
            AskSize: 1d,
            EventUnixNanoseconds: (clock.UtcNow.UtcDateTime - DateTime.UnixEpoch).Ticks * 100L,
            ReceiveMonotonicTicks: clock.MonotonicTimestamp,
            SourceSequence: index + 1));
}
