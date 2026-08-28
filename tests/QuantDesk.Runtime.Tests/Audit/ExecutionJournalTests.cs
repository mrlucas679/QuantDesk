using QuantDesk.Runtime.Audit;

namespace QuantDesk.Runtime.Tests.Audit;

public sealed class ExecutionJournalTests
{
    [Fact]
    public void Journal_AppendsAndReadsOrderedEvents()
    {
        string path = Path.Combine(Path.GetTempPath(), $"quantdesk-journal-{Guid.NewGuid():N}.jsonl");
        try
        {
            var journal = new ExecutionJournal(path);
            journal.Append(new ExecutionJournalEvent("OrderSubmitted", 0, DateTimeOffset.UtcNow, "qd-1", "hash-1", "req-1"));
            journal.Append(new ExecutionJournalEvent("OrderUnknown", 0, DateTimeOffset.UtcNow, "qd-1", "hash-2", "req-2"));

            IReadOnlyList<ExecutionJournalEvent> events = journal.ReadAll();
            Assert.Equal(2, events.Count);
            Assert.Equal(1, events[0].EventSequence);
            Assert.Equal("OrderUnknown", events[1].EventType);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void RecoversSequenceAfterRestart()
    {
        string path = Path.Combine(Path.GetTempPath(), $"quantdesk-journal-{Guid.NewGuid():N}.jsonl");
        try
        {
            var first = new ExecutionJournal(path);
            first.Append(new ExecutionJournalEvent("submitted", 0, DateTimeOffset.UtcNow, "order-1", "hash", null));
            var restarted = new ExecutionJournal(path);
            restarted.Append(new ExecutionJournalEvent("ack", 0, DateTimeOffset.UtcNow, "order-1", "hash", null));
            Assert.Equal([1L, 2L], restarted.ReadAll().Select(item => item.EventSequence));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void ReplayAppliesEventsInSequenceAndRejectsGaps()
    {
        string path = Path.Combine(Path.GetTempPath(), $"quantdesk-replay-{Guid.NewGuid():N}.jsonl");
        try
        {
            var journal = new ExecutionJournal(path);
            journal.Append(new ExecutionJournalEvent("a", 0, DateTimeOffset.UtcNow, "o", "h", null));
            journal.Append(new ExecutionJournalEvent("b", 0, DateTimeOffset.UtcNow, "o", "h", null));
            List<string> applied = [];
            Assert.Equal(2, new ExecutionJournalReplay(journal).Replay(item => applied.Add(item.EventType)));
            Assert.Equal(["a", "b"], applied);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void RestartRecoveryRejectsMalformedJournal()
    {
        string path = Path.Combine(Path.GetTempPath(), $"quantdesk-corrupt-{Guid.NewGuid():N}.jsonl");
        try
        {
            File.WriteAllText(path, "not-json\n");
            Assert.Throws<InvalidDataException>(() => new ExecutionJournal(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
