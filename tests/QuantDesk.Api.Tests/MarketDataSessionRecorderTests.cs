using Microsoft.Extensions.Logging.Abstractions;
using QuantDesk.Api.PaperTrading;
using QuantDesk.Domain.Market;
using QuantDesk.Domain.Replay;
using QuantDesk.Runtime.Replay;
using QuantDesk.Runtime.Time;

namespace QuantDesk.Api.Tests;

/// <summary>
/// A live session written to a log, and that log replaying to the same trace twice.
///
/// This is what section 22's gate was missing. The runner and the recorder both existed, were
/// tested, and had no production reference at all -- so determinism was proven against deciders
/// written for the runner's own tests and never against anything a session produced.
/// </summary>
public sealed class MarketDataSessionRecorderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"quantdesk-replay-{Guid.NewGuid():N}");

    public MarketDataSessionRecorderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void ARecordedSessionReplaysToTheSameTraceTwice()
    {
        // The claim the whole gate rests on, now made against events the market-data path produced
        // rather than against a decider written for the runner's tests.
        IReadOnlyList<ReplayEnvelope> log = RecordSession(events: 60);

        ReplayRunResult result = new ReplayRunner()
            .RunAndProveDeterministic(Manifest(), log, () => Decide);

        Assert.Equal(60, result.EventCount);
    }

    [Fact]
    public void TheLogSurvivesTheRoundTripThroughDisk()
    {
        var clock = new VirtualRuntimeClock(SessionStart);
        using var recorder = new MarketDataSessionRecorder(
            clock, NullLogger.Instance, _root);
        Assert.True(recorder.IsRecording);

        for (int index = 0; index < 25; index++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            recorder.Record(Quote(index, clock));
        }

        recorder.Dispose();

        IReadOnlyList<ReplayEnvelope> restored = ReplayEventRecorder.ReadFile(recorder.Path!);
        Assert.Equal(25, restored.Count);
        Assert.Equal(25L, recorder.Counts.Written);
        Assert.Equal(0L, recorder.Counts.Dropped);
    }

    [Fact]
    public void EachEventKindIsRecordedWithItsOwnTypeAndPayload()
    {
        // A replay that could not tell a quote from a trade would reconstruct a different session.
        var clock = new VirtualRuntimeClock(SessionStart);
        using var recorder = new MarketDataSessionRecorder(clock, NullLogger.Instance, _root);

        recorder.Record(Quote(1, clock));
        clock.Advance(TimeSpan.FromSeconds(1));
        recorder.Record(NormalizedMarketEvent.FromTrade(
            new TradeEvent(2, 0, 30_100d, 0.5d, Nanoseconds(clock), 0, 2)));
        clock.Advance(TimeSpan.FromSeconds(1));
        recorder.Record(NormalizedMarketEvent.FromOrderBook(
            new OrderBookEvent(3, 0, 30_090d, 30_110d, 4d, 5d, Nanoseconds(clock), 0, 3)));

        recorder.Dispose();
        IReadOnlyList<ReplayEnvelope> log = ReplayEventRecorder.ReadFile(recorder.Path!);

        Assert.Equal(["quote", "trade", "orderbook"], log.Select(entry => entry.EventType));
        Assert.All(log, entry => Assert.NotEmpty(entry.Payload));
        Assert.Equal(3, log.Select(entry => Convert.ToBase64String(entry.Payload)).Distinct().Count());
    }

    [Fact]
    public void HowLateTheRuntimeSawEachEventIsPreserved()
    {
        // Event time and receive time are different numbers, and a decision made on a quote that
        // was already stale is a different decision from the same one on a fresh quote.
        var clock = new VirtualRuntimeClock(SessionStart);
        using var recorder = new MarketDataSessionRecorder(clock, NullLogger.Instance, _root);

        long happened = Nanoseconds(clock);
        clock.Advance(TimeSpan.FromMilliseconds(750));
        recorder.Record(NormalizedMarketEvent.FromQuote(
            new QuoteEvent(1, 0, 30_000d, 30_010d, 1d, 1d, happened, 0, 1)));

        recorder.Dispose();
        ReplayEnvelope envelope = ReplayEventRecorder.ReadFile(recorder.Path!).Single();

        Assert.Equal(happened, envelope.EventUnixNanoseconds);
        Assert.Equal(750_000_000L, envelope.ReceiveOffsetNanoseconds);
    }

    [Fact]
    public void AnUnwritableDirectoryDisablesRecordingRatherThanStoppingTheSession()
    {
        // A session that cannot be recorded still trades. Refusing to start because a log directory
        // is unwritable would turn an observability problem into an outage.
        string file = Path.Combine(_root, "not-a-directory");
        File.WriteAllText(file, "occupied");

        using var recorder = new MarketDataSessionRecorder(
            new VirtualRuntimeClock(SessionStart), NullLogger.Instance, file);

        Assert.False(recorder.IsRecording);
        recorder.Record(Quote(1, new VirtualRuntimeClock(SessionStart)));
        Assert.Equal(0L, recorder.Counts.Written);
    }

    [Fact]
    public void EventsArrivingInTheSameNanosecondKeepTheirArrivalOrder()
    {
        // Several events routinely share a nanosecond on a quiet feed. The ingress sequence is what
        // fixes their order, and a replay whose input order is negotiable reproduces nothing.
        var clock = new VirtualRuntimeClock(SessionStart);
        using var recorder = new MarketDataSessionRecorder(clock, NullLogger.Instance, _root);

        for (int index = 0; index < 5; index++) recorder.Record(Quote(index, clock));
        recorder.Dispose();

        IReadOnlyList<ReplayEnvelope> log = ReplayEventRecorder.ReadFile(recorder.Path!);
        Assert.Equal([1L, 2L, 3L, 4L, 5L], log.Select(entry => entry.IngressSequence));
        Assert.Single(log.Select(entry => entry.EventUnixNanoseconds).Distinct());
    }

    // ------------------------------------------------------------------------------- fixtures

    private static readonly DateTimeOffset SessionStart =
        new(2026, 9, 3, 9, 30, 0, TimeSpan.Zero);

    private IReadOnlyList<ReplayEnvelope> RecordSession(int events)
    {
        var clock = new VirtualRuntimeClock(SessionStart);
        using var recorder = new MarketDataSessionRecorder(clock, NullLogger.Instance, _root);

        for (int index = 0; index < events; index++)
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            recorder.Record(Quote(index, clock));
        }

        recorder.Dispose();
        return ReplayEventRecorder.ReadFile(recorder.Path!);
    }

    private static NormalizedMarketEvent Quote(int index, IRuntimeClock clock) =>
        NormalizedMarketEvent.FromQuote(new QuoteEvent(
            EventId: index + 1,
            InstrumentSlot: 0,
            Bid: 30_000d + index,
            Ask: 30_010d + index,
            BidSize: 1d,
            AskSize: 1d,
            EventUnixNanoseconds: Nanoseconds(clock),
            ReceiveMonotonicTicks: clock.MonotonicTimestamp,
            SourceSequence: index + 1));

    private static long Nanoseconds(IRuntimeClock clock) =>
        (clock.UtcNow.UtcDateTime - DateTime.UnixEpoch).Ticks * 100L;

    /// <summary>A decider that reads the injected clock, which is what a replay has to reproduce.</summary>
    private static (string Code, string Payload)? Decide(IRuntimeClock clock, ReplayEnvelope envelope) =>
        clock.UtcNow - SessionStart >= TimeSpan.FromSeconds(60)
            ? ("EXIT", clock.UtcNow.ToString("O"))
            : ("HOLD", clock.UtcNow.ToString("O"));

    private static ReplayManifest Manifest() => new(
        "config-abc", "artifacts-abc", "policy-v1", 20260903,
        ReplayEvidenceClass.PassiveHistoricalReplay);
}
