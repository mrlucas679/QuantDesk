using QuantDesk.Runtime.Persistence;

namespace QuantDesk.Runtime.Tests.Persistence;

public sealed class AutonomousExecutionStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"qd-auto-{Guid.NewGuid():N}.json");

    [Fact]
    public void PersistsRecordAndFencesExecutionAndClientIds()
    {
        var store = new AutonomousExecutionStore(_path);
        var record = new AutonomousExecutionRecord("AUTO-1", "strategy", "BTC/USD", "entry-1", "exit-1",
            DateTimeOffset.UnixEpoch) { EntryQuantity = .001m };

        Assert.True(store.TryCreate(record));
        AutonomousExecutionRecord restored = new AutonomousExecutionStore(_path).Find("AUTO-1")!;

        Assert.Equal(record.EntryClientOrderId, restored.EntryClientOrderId);
        Assert.Equal(.001m, restored.EntryQuantity);
        Assert.False(store.TryCreate(record with { ExecutionId = "AUTO-2" }));
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
        if (File.Exists(_path + ".tmp")) File.Delete(_path + ".tmp");
    }
}
