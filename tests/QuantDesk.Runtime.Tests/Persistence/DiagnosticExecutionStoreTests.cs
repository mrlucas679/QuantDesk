using QuantDesk.Runtime.Persistence;

namespace QuantDesk.Runtime.Tests.Persistence;

public sealed class DiagnosticExecutionStoreTests
{
    [Fact]
    public void Round_trip_and_restart_preserve_state()
    {
        string path = Path.Combine(Path.GetTempPath(), $"qd-{Guid.NewGuid():N}.json");
        try
        {
            var first = new DiagnosticExecutionStore(path);
            var record = new DiagnosticExecutionRecord("exp", "DiagnosticExecution", "BTC/USD", "Holding", 5m,
                TimeSpan.FromMinutes(2), DateTimeOffset.UtcNow, "entry-id", "exit-id")
            { ScheduledExitAt = DateTimeOffset.UtcNow.AddMinutes(2), EntryFilledQuantity = 0.001m };
            first.Record(record);
            Assert.True(first.TryReserve("exp", "entry-id"));
            Assert.False(first.TryReserve("exp", "entry-id"));
            var restarted = new DiagnosticExecutionStore(path);
            Assert.Equal(record.ExperimentId, restarted.Find("exp")!.ExperimentId);
            Assert.Equal(record.ScheduledExitAt, restarted.Find("exp")!.ScheduledExitAt);
            Assert.False(restarted.TryReserve("exp", "entry-id"));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
