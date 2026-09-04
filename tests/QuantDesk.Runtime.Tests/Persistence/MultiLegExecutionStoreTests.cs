using QuantDesk.Domain.Execution;
using QuantDesk.Domain.Trading;
using QuantDesk.Runtime.Persistence;

namespace QuantDesk.Runtime.Tests.Persistence;

public sealed class MultiLegExecutionStoreTests
{
    [Fact]
    public void CorruptPersistedJsonFailsClosedWithoutReplacingTheEvidenceFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"qd-mleg-corrupt-{Guid.NewGuid():N}.json");
        try
        {
            const string corruptPayload = "{ definitely not valid JSON";
            File.WriteAllText(path, corruptPayload);
            var store = new MultiLegExecutionStore(path);

            Assert.False(store.IsAvailable());
            Assert.Throws<System.Text.Json.JsonException>(() => store.Find("OPTIONS-CORRUPT"));
            Assert.Equal(corruptPayload, File.ReadAllText(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
        }
    }

    [Fact]
    public void DuplicateClientOrderIdentityIsRejectedAcrossStoreInstances()
    {
        string path = Path.Combine(Path.GetTempPath(), $"qd-mleg-duplicate-{Guid.NewGuid():N}.json");
        try
        {
            MultiLegExecutionRecord record = Record("OPTIONS-ONE", "qd-opt-entry", "qd-opt-exit");
            Assert.True(new MultiLegExecutionStore(path).TryCreate(record));

            bool inserted = new MultiLegExecutionStore(path).TryCreate(
                Record("OPTIONS-TWO", "qd-opt-entry", "qd-opt-exit-two"));

            Assert.False(inserted);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
        }
    }

    [Fact]
    public async Task ConcurrentStoreInstancesAllowOnlyOneReservation()
    {
        string path = Path.Combine(Path.GetTempPath(), $"qd-mleg-race-{Guid.NewGuid():N}.json");
        try
        {
            var start = new ManualResetEventSlim(false);
            Task<bool>[] attempts =
            [
                Task.Run(() => { start.Wait(); return new MultiLegExecutionStore(path).TryCreate(
                    Record("OPTIONS-RACE-A", "qd-opt-race-entry", "qd-opt-race-exit")); }),
                Task.Run(() => { start.Wait(); return new MultiLegExecutionStore(path).TryCreate(
                    Record("OPTIONS-RACE-B", "qd-opt-race-entry", "qd-opt-race-exit")); })
            ];
            start.Set();

            bool[] outcomes = await Task.WhenAll(attempts);

            Assert.Equal(1, outcomes.Count(result => result));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
        }
    }

    private static MultiLegExecutionRecord Record(string executionId, string entryId, string exitId) => new(
        executionId, "spy-vertical", MultiLegExecutionState.EntryReserved,
        new MultiLegExecutionCommand(entryId, 1, ExecutionOrderType.Limit, ExecutionTimeInForce.Day,
            1.25m, Legs()),
        new MultiLegExecutionCommand(exitId, 1, ExecutionOrderType.Limit, ExecutionTimeInForce.Day,
            1.50m, Legs()),
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static IReadOnlyList<MultiLegExecutionLeg> Legs() =>
    [
        new("SPY260904C00650000", 1, OrderSide.Buy, PositionIntent.BuyToOpen),
        new("SPY260904C00655000", 1, OrderSide.Sell, PositionIntent.SellToOpen)
    ];
}
