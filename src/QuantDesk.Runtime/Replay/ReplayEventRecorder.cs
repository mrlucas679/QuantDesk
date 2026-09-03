using System.Globalization;
using System.Text;
using System.Text.Json;
using QuantDesk.Domain.Replay;
using QuantDesk.Runtime.Time;

namespace QuantDesk.Runtime.Replay;

/// <summary>
/// Records what the runtime saw, in an order a replay can reproduce.
///
/// Why the sequence is a counter and not a timestamp
/// -------------------------------------------------
/// Two events can arrive in the same nanosecond, and on a quiet feed several routinely do. A log
/// ordered by timestamp leaves those in whatever order the sort happened to produce, which is
/// stable within one run and not between runs -- so the replay reproduces a different input than
/// the one recorded and reports it as a faithful reproduction.
///
/// A counter fixes the order at the moment of arrival, which is the only moment that knows it.
///
/// Event time and receive time are both kept
/// -----------------------------------------
/// The envelope carries when the event happened and how long after that the runtime saw it. Those
/// are different numbers and collapsing them loses the thing worth knowing: a decision made on a
/// quote that was already 800 milliseconds old is a different decision from the same one made on a
/// fresh quote, and only the offset says which happened.
///
/// The recorder never decides
/// --------------------------
/// It observes. A recorder that filtered, deduplicated or normalised would be recording its own
/// opinion of the session rather than the session, and the first thing anyone would want to replay
/// is the case where that opinion was wrong.
/// </summary>
public sealed class ReplayEventRecorder(IRuntimeClock clock, int schemaVersion = 1)
{
    private readonly Lock _gate = new();
    private readonly List<ReplayEnvelope> _events = [];
    private long _nextSequence = 1;

    /// <summary>How many events have been recorded.</summary>
    public int Count
    {
        get
        {
            lock (_gate) return _events.Count;
        }
    }

    /// <summary>
    /// Records one event and returns the envelope written.
    /// </summary>
    /// <param name="source">Which feed or subsystem produced it.</param>
    /// <param name="eventType">What kind of thing it is, in the vocabulary the decider reads.</param>
    /// <param name="eventUnixNanoseconds">When it happened, at the source.</param>
    /// <param name="payload">The event itself, opaque to the recorder.</param>
    public ReplayEnvelope Record(
        string source, string eventType, long eventUnixNanoseconds, ReadOnlySpan<byte> payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);

        long receivedNanoseconds = ToUnixNanoseconds(clock.UtcNow);

        // Recorded as measured, negative included. A venue clock running ahead of ours makes this
        // genuinely negative, and the replay clock is reconstructed as event + offset -- so clamping
        // here would not hide an anomaly, it would move the receive time by however far the venue
        // was ahead and desynchronise the timeline the whole replay advances along.
        long offset = receivedNanoseconds - eventUnixNanoseconds;

        lock (_gate)
        {
            var envelope = new ReplayEnvelope(
                schemaVersion,
                _nextSequence++,
                source,
                eventUnixNanoseconds,
                offset,
                eventType,
                payload.ToArray());

            _events.Add(envelope);
            return envelope;
        }
    }

    /// <summary>The recorded log, in ingress order.</summary>
    public IReadOnlyList<ReplayEnvelope> Snapshot()
    {
        lock (_gate) return [.. _events];
    }

    /// <summary>
    /// Writes the log as one JSON object per line.
    ///
    /// Line-per-event so a session that is killed mid-write leaves a log that is short rather than
    /// unparseable, and so a long session can be read without holding all of it in memory. The
    /// payload is base64 because it is arbitrary bytes and JSON is text.
    /// </summary>
    public static void Write(TextWriter writer, IReadOnlyList<ReplayEnvelope> log)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(log);

        foreach (ReplayEnvelope envelope in log)
        {
            writer.WriteLine(JsonSerializer.Serialize(new ReplayEnvelopeLine(
                envelope.SchemaVersion,
                envelope.IngressSequence,
                envelope.Source,
                envelope.EventUnixNanoseconds,
                envelope.ReceiveOffsetNanoseconds,
                envelope.EventType,
                Convert.ToBase64String(envelope.Payload))));
        }
    }

    /// <summary>
    /// Reads a log back, refusing a line it cannot read rather than skipping it.
    ///
    /// Skipping would produce a shorter log that replays cleanly and reproduces something the
    /// session never did, which is worse than failing to read the file.
    /// </summary>
    public static IReadOnlyList<ReplayEnvelope> Read(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var log = new List<ReplayEnvelope>();
        int lineNumber = 0;

        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;

            ReplayEnvelopeLine? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<ReplayEnvelopeLine>(line);
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException(
                    $"Replay log line {lineNumber.ToString(CultureInfo.InvariantCulture)} is not "
                    + "valid JSON.", exception);
            }

            if (parsed is null)
            {
                throw new InvalidDataException(
                    $"Replay log line {lineNumber.ToString(CultureInfo.InvariantCulture)} is empty.");
            }

            var envelope = new ReplayEnvelope(
                parsed.SchemaVersion,
                parsed.IngressSequence,
                parsed.Source ?? string.Empty,
                parsed.EventUnixNanoseconds,
                parsed.ReceiveOffsetNanoseconds,
                parsed.EventType ?? string.Empty,
                Convert.FromBase64String(parsed.PayloadBase64 ?? string.Empty));

            if (!envelope.IsValid())
            {
                throw new InvalidDataException(
                    $"Replay log line {lineNumber.ToString(CultureInfo.InvariantCulture)} is missing "
                    + "a field the runner needs.");
            }

            log.Add(envelope);
        }

        return log;
    }

    /// <summary>Reads a log from a file.</summary>
    public static IReadOnlyList<ReplayEnvelope> ReadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var reader = new StreamReader(path, Encoding.UTF8);
        return Read(reader);
    }

    /// <summary>Writes a log to a file.</summary>
    public static void WriteFile(string path, IReadOnlyList<ReplayEnvelope> log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var writer = new StreamWriter(path, append: false, Encoding.UTF8);
        Write(writer, log);
    }

    private static long ToUnixNanoseconds(DateTimeOffset moment) =>
        (moment.UtcDateTime - DateTime.UnixEpoch).Ticks * 100L;

    /// <summary>The wire shape of one recorded event.</summary>
    private sealed record ReplayEnvelopeLine(
        int SchemaVersion,
        long IngressSequence,
        string? Source,
        long EventUnixNanoseconds,
        long ReceiveOffsetNanoseconds,
        string? EventType,
        string? PayloadBase64);
}
