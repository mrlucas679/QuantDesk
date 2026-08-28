using QuantDesk.Domain.Numerics;
using QuantDesk.Domain.Portfolio;
using QuantDesk.Runtime.Persistence;

namespace QuantDesk.Runtime.Tests.Persistence;

public sealed class PortfolioSnapshotStoreTests
{
    [Fact]
    public void SavesAndLoadsVersionedSnapshot()
    {
        string path = Path.Combine(Path.GetTempPath(), $"quantdesk-snapshot-{Guid.NewGuid():N}.json");
        try
        {
            PortfolioSnapshot original = new(7, new Usd(101), new Usd(99), new Usd(100), Usd.Zero, Usd.Zero, Usd.Zero, Usd.Zero, 0, 0, 0, 0, 0, 0, 0, []);
            var store = new PortfolioSnapshotStore(path);
            store.Save(original);
            PortfolioSnapshot loaded = store.Load();
            Assert.Equal(original.Version, loaded.Version);
            Assert.Equal(original.Equity, loaded.Equity);
            Assert.Equal(original.Cash, loaded.Cash);
            Assert.Empty(loaded.Positions);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
