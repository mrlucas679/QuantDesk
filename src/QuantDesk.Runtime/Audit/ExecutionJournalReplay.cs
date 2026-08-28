namespace QuantDesk.Runtime.Audit;

public sealed class ExecutionJournalReplay(ExecutionJournal journal)
{
    public int Replay(Action<ExecutionJournalEvent> apply)
    {
        ArgumentNullException.ThrowIfNull(apply);
        IReadOnlyList<ExecutionJournalEvent> events = journal.ReadAll();
        long expected = 1;
        foreach (ExecutionJournalEvent journalEvent in events.OrderBy(item => item.EventSequence))
        {
            if (journalEvent.EventSequence != expected)
                throw new InvalidDataException($"Execution journal sequence gap at {expected}.");
            apply(journalEvent);
            expected++;
        }
        return events.Count;
    }
}
