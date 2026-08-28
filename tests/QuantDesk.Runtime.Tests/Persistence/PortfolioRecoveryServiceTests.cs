using QuantDesk.Domain.Numerics;
using QuantDesk.Domain.Portfolio;
using QuantDesk.Runtime.Audit;
using QuantDesk.Runtime.Persistence;

namespace QuantDesk.Runtime.Tests.Persistence;

public sealed class PortfolioRecoveryServiceTests
{
    [Fact]
    public void RecoversSnapshotAndReplaysJournalTogether()
    {
        string root = Path.Combine(Path.GetTempPath(), $"quantdesk-recovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var snapshot = new PortfolioSnapshotStore(Path.Combine(root, "portfolio.json"));
            PortfolioSnapshot expected = new(3, new Usd(100), new Usd(100), new Usd(100), Usd.Zero, Usd.Zero, Usd.Zero, Usd.Zero, 0, 0, 0, 0, 0, 0, 0, []);
            snapshot.Save(expected);
            var journal = new ExecutionJournal(Path.Combine(root, "execution.jsonl"));
            journal.Append(new ExecutionJournalEvent("submitted", 0, DateTimeOffset.UtcNow, "order", "hash", null));
            var service = new PortfolioRecoveryService(snapshot, journal);
            int applied = 0;
            PortfolioRecoveryResult result = service.Recover(_ => applied++);
            Assert.Equal(expected.Version, result.Snapshot.Version);
            Assert.Equal(1, applied);
            Assert.Equal(1, result.ReplayedEvents);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
