using System.Text.Json;

namespace QuantDesk.Runtime.Audit;

public sealed record ExecutionJournalEvent(
    string EventType,
    long EventSequence,
    DateTimeOffset RecordedUtc,
    string ClientOrderId,
    string PayloadHash,
    string? RequestId);

public sealed class ExecutionJournal
{
    private readonly string path;
    private readonly Lock _gate = new();
    private long _nextSequence;

    public ExecutionJournal(string path) : this(path, recoverSequence: true) { }

    private ExecutionJournal(string path, bool recoverSequence)
    {
        this.path = path;
        if (recoverSequence && File.Exists(path))
        {
            try
            {
                _nextSequence = File.ReadLines(path)
                    .Where(line => !string.IsNullOrWhiteSpace(line))
                    .Select(line => JsonSerializer.Deserialize<ExecutionJournalEvent>(line)?.EventSequence
                        ?? throw new InvalidDataException("Execution journal contains malformed JSON."))
                    .DefaultIfEmpty()
                    .Max();
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("Execution journal contains malformed JSON.", exception);
            }
        }
    }

    public void Append(ExecutionJournalEvent journalEvent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journalEvent.EventType);
        ArgumentException.ThrowIfNullOrWhiteSpace(journalEvent.ClientOrderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(journalEvent.PayloadHash);

        lock (_gate)
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            long sequence = ++_nextSequence;
            var persisted = journalEvent with { EventSequence = sequence };
            string line = JsonSerializer.Serialize(persisted) + Environment.NewLine;
            using FileStream stream = new(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            using StreamWriter writer = new(stream);
            writer.Write(line);
            writer.Flush();
            stream.Flush(true);
        }
    }

    public IReadOnlyList<ExecutionJournalEvent> ReadAll()
    {
        lock (_gate)
        {
            if (!File.Exists(path)) return [];
            return File.ReadLines(path)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => JsonSerializer.Deserialize<ExecutionJournalEvent>(line)
                    ?? throw new InvalidDataException("Execution journal contains an empty event."))
                .ToArray();
        }
    }
}
