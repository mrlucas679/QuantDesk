using QuantDesk.Domain.Portfolio;
using QuantDesk.Runtime.Audit;

namespace QuantDesk.Runtime.Persistence;

public sealed record PortfolioRecoveryResult(PortfolioSnapshot Snapshot, int ReplayedEvents);

public sealed class PortfolioRecoveryService(
    PortfolioSnapshotStore snapshotStore,
    ExecutionJournal journal)
{
    public PortfolioRecoveryResult Recover(Action<ExecutionJournalEvent> applyEvent)
    {
        ArgumentNullException.ThrowIfNull(applyEvent);
        PortfolioSnapshot snapshot = snapshotStore.Load();
        int replayed = new ExecutionJournalReplay(journal).Replay(applyEvent);
        return new PortfolioRecoveryResult(snapshot, replayed);
    }
}
