using System.Text.Json;
using QuantDesk.Domain.Market;
using QuantDesk.Domain.Replay;
using QuantDesk.Runtime.Replay;
using QuantDesk.Runtime.Time;

namespace QuantDesk.Api.PaperTrading;

/// <summary>
/// Writes the market-data stream to a replay log, so a session can be run again.
///
/// The gap this closes
/// -------------------
/// Section 22 makes deterministic replay a release gate. The runner and the recorder were built,
/// verified and connected to nothing -- both had zero production references -- so the gate was
/// proven against deciders written for its own tests and never against a real session. Nothing
/// recorded, so nothing could be replayed.
///
/// The obstacle was the clock, and it is gone. Every timestamp on this path now comes from
/// IRuntimeClock, so the times written here are the times the decision path saw.
///
/// What is recorded, and what is not
/// ---------------------------------
/// The normalised event as the stream produced it, with the venue's own event time and the offset
/// to when this process saw it. Not the raw frame: the runtime decides on the normalised form, and
/// a log of the wire format would replay the parser rather than the decision.
///
/// Recording never blocks the stream
/// ---------------------------------
/// A failed write is logged and dropped, and the market-data loop continues. A recorder that could
/// stall the path it observes would be a new way to lose a session, and this exists to make losing
/// one recoverable rather than to add a reason for it. The count of dropped events is reported, so
/// a log with holes in it cannot pass as complete.
///
/// Rotation is per session
/// -----------------------
/// One file per process start, named for the moment it began. Appending to a single growing file
/// would mean a replay had to find where one session ended and the next began, and the boundary
/// between two runs is exactly where the state a replay depends on is discontinuous.
/// </summary>
public sealed class MarketDataSessionRecorder : IDisposable
{
    private readonly ReplayEventRecorder _recorder;
    private readonly StreamWriter? _writer;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private long _dropped;
    private long _written;
    private bool _disposed;

    public MarketDataSessionRecorder(IRuntimeClock clock, ILogger logger, string? root = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _recorder = new ReplayEventRecorder(clock);
        _logger = logger;

        string directory = root
            ?? Environment.GetEnvironmentVariable("QUANTDESK_REPLAY_LOG_ROOT")
            ?? "/app/replay-logs";

        try
        {
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(
                directory, $"session-{clock.UtcNow:yyyyMMdd-HHmmss}.jsonl");
            _writer = new StreamWriter(Path, append: false) { AutoFlush = true };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A session that cannot be recorded still trades. Refusing to start because a log
            // directory is unwritable would turn an observability problem into an outage.
            _logger.LogWarning(exception, "Replay recording is unavailable; the session will not be replayable.");
        }
    }

    /// <summary>Where this session is being written, or null when recording is unavailable.</summary>
    public string? Path { get; }

    /// <summary>True when events are reaching disk.</summary>
    public bool IsRecording => _writer is not null;

    /// <summary>How many events were recorded, and how many could not be.</summary>
    public (long Written, long Dropped) Counts
    {
        get { lock (_gate) return (_written, _dropped); }
    }

    /// <summary>
    /// Records one event. Never throws, and never blocks the stream for long.
    /// </summary>
    public void Record(in NormalizedMarketEvent marketEvent)
    {
        if (_writer is null) return;

        lock (_gate)
        {
            if (_disposed) return;
        }

        (string type, int slot, long eventNanoseconds, byte[] payload) = Describe(marketEvent);

        try
        {
            ReplayEnvelope envelope = _recorder.Record(
                source: $"alpaca-crypto:{slot}",
                eventType: type,
                eventUnixNanoseconds: eventNanoseconds,
                payload: payload);

            lock (_gate)
            {
                ReplayEventRecorder.Write(_writer, [envelope]);
                _written++;
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            lock (_gate) _dropped++;

            // Logged once every thousand, because a failing disk would otherwise produce a log
            // line per market event and bury everything else.
            if (Interlocked.Read(ref _dropped) % 1_000 == 1)
                _logger.LogWarning(exception, "Replay recording is dropping events.");
        }
    }

    /// <summary>
    /// The event as the runtime sees it, flattened for the log.
    ///
    /// The payload is the numbers a decision reads, serialised in a fixed field order. A replay
    /// compares payload hashes, so a serialisation whose field order depended on anything would
    /// make two runs of the same log disagree.
    /// </summary>
    private static (string Type, int Slot, long EventNanoseconds, byte[] Payload) Describe(
        in NormalizedMarketEvent marketEvent) => marketEvent.Kind switch
    {
        MarketEventKind.Quote => (
            "quote",
            marketEvent.Quote.InstrumentSlot,
            marketEvent.Quote.EventUnixNanoseconds,
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                marketEvent.Quote.EventId,
                marketEvent.Quote.Bid,
                marketEvent.Quote.Ask,
                marketEvent.Quote.BidSize,
                marketEvent.Quote.AskSize,
                marketEvent.Quote.SourceSequence,
            })),

        MarketEventKind.Trade => (
            "trade",
            marketEvent.Trade.InstrumentSlot,
            marketEvent.Trade.EventUnixNanoseconds,
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                marketEvent.Trade.EventId,
                marketEvent.Trade.Price,
                marketEvent.Trade.Size,
                marketEvent.Trade.SourceSequence,
            })),

        MarketEventKind.OrderBook => (
            "orderbook",
            marketEvent.OrderBook.InstrumentSlot,
            marketEvent.OrderBook.EventUnixNanoseconds,
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                marketEvent.OrderBook.EventId,
                marketEvent.OrderBook.BestBid,
                marketEvent.OrderBook.BestAsk,
                marketEvent.OrderBook.BidDepth,
                marketEvent.OrderBook.AskDepth,
                marketEvent.OrderBook.SourceSequence,
            })),

        _ => ("unknown", 0, 0L, []),
    };

    /// <summary>
    /// Closes the log. Safe to call more than once.
    ///
    /// Idempotent because it will be: the host disposes its singletons, and a caller that closes a
    /// session explicitly before shutdown is doing the right thing. Flushing an already-disposed
    /// writer throws, so the first version turned an ordinary double dispose into an exception on
    /// the shutdown path.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;

            _writer?.Flush();
            _writer?.Dispose();
        }
    }
}
