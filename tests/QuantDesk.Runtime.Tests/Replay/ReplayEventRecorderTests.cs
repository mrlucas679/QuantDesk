using System.Text;
using QuantDesk.Domain.Replay;
using QuantDesk.Runtime.Replay;
using QuantDesk.Runtime.Time;

namespace QuantDesk.Runtime.Tests.Replay;

/// <summary>
/// Recording a session so it can be replayed, and the ways a recording stops being one.
/// </summary>
public sealed class ReplayEventRecorderTests
{
    private static readonly DateTimeOffset SessionStart =
        new(2026, 9, 3, 13, 30, 0, TimeSpan.Zero);

    [Fact]
    public void EventsArrivingInTheSameNanosecondStillGetADistinctOrder()
    {
        // The reason the sequence is a counter rather than a timestamp. On a quiet feed several
        // events routinely share a nanosecond, and a log ordered by time leaves those in whatever
        // order the sort produced -- stable within one run and not between runs, so the replay
        // reproduces a different input than the one recorded and calls it faithful.
        var recorder = new ReplayEventRecorder(new VirtualRuntimeClock(SessionStart));
        long moment = Nanoseconds(SessionStart);

        ReplayEnvelope first = recorder.Record("feed", "quote", moment, [1]);
        ReplayEnvelope second = recorder.Record("feed", "quote", moment, [2]);
        ReplayEnvelope third = recorder.Record("feed", "quote", moment, [3]);

        Assert.Equal([1L, 2L, 3L], new[] { first, second, third }.Select(e => e.IngressSequence));
        Assert.Equal(moment, third.EventUnixNanoseconds);
    }

    [Fact]
    public void HowLateTheRuntimeSawTheEventIsKeptSeparateFromWhenItHappened()
    {
        // Collapsing them loses the thing worth knowing. A decision made on a quote already 800ms
        // old is a different decision from the same one on a fresh quote, and only the offset says
        // which happened.
        var clock = new VirtualRuntimeClock(SessionStart);
        var recorder = new ReplayEventRecorder(clock);
        long happened = Nanoseconds(SessionStart);

        clock.Advance(TimeSpan.FromMilliseconds(800));
        ReplayEnvelope envelope = recorder.Record("feed", "quote", happened, [1]);

        Assert.Equal(happened, envelope.EventUnixNanoseconds);
        Assert.Equal(800_000_000L, envelope.ReceiveOffsetNanoseconds);
    }

    [Fact]
    public void AnEventStampedAheadOfOurClockKeepsItsNegativeOffset()
    {
        // A venue clock running ahead of ours. The offset is how the receive time is reconstructed
        // -- event plus offset -- and the replay clock advances along that, so clamping the negative
        // away would not hide an anomaly: it would move the recorded receive time five seconds later
        // than the moment the runtime actually saw the event, and desynchronise the whole timeline.
        var recorder = new ReplayEventRecorder(new VirtualRuntimeClock(SessionStart));

        ReplayEnvelope envelope = recorder.Record(
            "feed", "quote", Nanoseconds(SessionStart.AddSeconds(5)), [1]);

        Assert.Equal(-5_000_000_000L, envelope.ReceiveOffsetNanoseconds);
        Assert.Equal(Nanoseconds(SessionStart), envelope.ReceiveUnixNanoseconds);
        Assert.True(envelope.IsValid());
    }

    [Fact]
    public void ARecordedSessionSurvivesTheRoundTripThroughItsLogFile()
    {
        // The replay is only as good as the file, and a log that loses a field on the way to disk
        // replays something the session never did.
        var recorder = new ReplayEventRecorder(new VirtualRuntimeClock(SessionStart));
        for (int index = 0; index < 25; index++)
        {
            recorder.Record(
                "crypto-quotes",
                index % 3 == 0 ? "quote" : "trade",
                Nanoseconds(SessionStart.AddSeconds(index)),
                [(byte)index, 0xFF, 0x00]);
        }

        IReadOnlyList<ReplayEnvelope> original = recorder.Snapshot();

        var buffer = new StringWriter();
        ReplayEventRecorder.Write(buffer, original);
        IReadOnlyList<ReplayEnvelope> restored =
            ReplayEventRecorder.Read(new StringReader(buffer.ToString()));

        Assert.Equal(original.Count, restored.Count);
        foreach ((ReplayEnvelope before, ReplayEnvelope after) in original.Zip(restored))
        {
            Assert.Equal(before.IngressSequence, after.IngressSequence);
            Assert.Equal(before.EventUnixNanoseconds, after.EventUnixNanoseconds);
            Assert.Equal(before.ReceiveOffsetNanoseconds, after.ReceiveOffsetNanoseconds);
            Assert.Equal(before.EventType, after.EventType);
            Assert.Equal(before.Source, after.Source);
            Assert.Equal(before.Payload, after.Payload);
        }
    }

    [Fact]
    public void ARestoredLogReplaysToTheSameTraceAsTheOneInMemory()
    {
        // The end-to-end claim for the recorder: what went to disk is what gets replayed.
        // The clock advances, because the recorder stamps a monotonic elapsed figure and a session
        // in which no time passes carries no timeline for a replay to advance along.
        var clock = new VirtualRuntimeClock(SessionStart);
        var recorder = new ReplayEventRecorder(clock);
        for (int index = 0; index < 30; index++)
        {
            recorder.Record(
                "crypto-quotes", "quote", Nanoseconds(SessionStart.AddMinutes(index)), [(byte)index]);
            clock.Advance(TimeSpan.FromMinutes(1));
        }

        var buffer = new StringWriter();
        ReplayEventRecorder.Write(buffer, recorder.Snapshot());
        IReadOnlyList<ReplayEnvelope> restored =
            ReplayEventRecorder.Read(new StringReader(buffer.ToString()));

        var runner = new ReplayRunner();
        ReplayManifest manifest = Manifest();

        Assert.Equal(
            runner.Run(manifest, recorder.Snapshot(), Decide).DeterministicTraceHash,
            runner.Run(manifest, restored, Decide).DeterministicTraceHash);
    }

    [Fact]
    public void ALineThatCannotBeReadFailsRatherThanBeingSkipped()
    {
        // Skipping produces a shorter log that replays cleanly and reproduces something the session
        // never did, which is worse than failing to read the file.
        var recorder = new ReplayEventRecorder(new VirtualRuntimeClock(SessionStart));
        recorder.Record("feed", "quote", Nanoseconds(SessionStart), [1]);

        var buffer = new StringWriter();
        ReplayEventRecorder.Write(buffer, recorder.Snapshot());
        string corrupted = buffer.ToString() + "{ not json" + Environment.NewLine;

        Assert.Throws<InvalidDataException>(() =>
            ReplayEventRecorder.Read(new StringReader(corrupted)));
    }

    [Fact]
    public void ALineMissingAFieldTheRunnerNeedsIsRefused()
    {
        const string line =
            """{"SchemaVersion":1,"IngressSequence":1,"Source":"","EventUnixNanoseconds":1,"ReceiveOffsetNanoseconds":0,"EventType":"quote","PayloadBase64":""}""";

        Assert.Throws<InvalidDataException>(() => ReplayEventRecorder.Read(new StringReader(line)));
    }

    [Fact]
    public void ARecordingWrittenToDiskCanBeReadBackByPath()
    {
        string path = Path.Combine(Path.GetTempPath(), $"quantdesk-replay-{Guid.NewGuid():N}.jsonl");
        try
        {
            var recorder = new ReplayEventRecorder(new VirtualRuntimeClock(SessionStart));
            recorder.Record("feed", "quote", Nanoseconds(SessionStart), Encoding.UTF8.GetBytes("hi"));

            ReplayEventRecorder.WriteFile(path, recorder.Snapshot());

            Assert.Single(ReplayEventRecorder.ReadFile(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void TheRecorderObservesRatherThanDecides()
    {
        // A recorder that filtered or deduplicated would be recording its opinion of the session
        // rather than the session, and the first thing anyone wants to replay is the case where
        // that opinion was wrong.
        var recorder = new ReplayEventRecorder(new VirtualRuntimeClock(SessionStart));
        long moment = Nanoseconds(SessionStart);

        recorder.Record("feed", "quote", moment, [7]);
        recorder.Record("feed", "quote", moment, [7]);

        Assert.Equal(2, recorder.Count);
    }

    private static (string Code, string Payload)? Decide(
        IRuntimeClock clock, ReplayEnvelope envelope) =>
        ("HOLD", clock.UtcNow.ToString("O"));

    private static ReplayManifest Manifest() => new(
        "config-abc", "artifacts-abc", "policy-v1", 1, ReplayEvidenceClass.PassiveHistoricalReplay);

    private static long Nanoseconds(DateTimeOffset moment) =>
        (moment.UtcDateTime - DateTime.UnixEpoch).Ticks * 100L;
}
