using QuantDesk.Domain.Execution;
using QuantDesk.Domain.Runtime;
using QuantDesk.Runtime.Modes;
using QuantDesk.Runtime.Reconciliation;

namespace QuantDesk.Runtime.Tests.Reconciliation;

public sealed class ReconciliationServiceTests
{
    [Fact]
    public void Reconcile_UnknownBrokerOrderHaltsEntries()
    {
        var mode = new RuntimeModeState();
        var service = new ReconciliationService(mode);
        var input = new ReconciliationInput(
            new HashSet<string>(StringComparer.Ordinal) { "qd-local-1" },
            new Dictionary<int, decimal>(),
            [new BrokerOrderSnapshot("broker-1", "external-1", "open", 0, null)],
            []);

        ReconciliationResult result = service.Reconcile(input);

        Assert.False(result.IsReconciled);
        Assert.Contains("UNKNOWN_BROKER_ORDER", result.MismatchCodes);
        Assert.Equal(SystemMode.EntryHalted, mode.Snapshot().Mode);
    }

    [Fact]
    public void Reconcile_MatchingOrdersAndPositionsEntersReady()
    {
        var mode = new RuntimeModeState();
        var service = new ReconciliationService(mode);
        var input = new ReconciliationInput(
            new HashSet<string>(StringComparer.Ordinal) { "qd-local-1" },
            new Dictionary<int, decimal> { [0] = 2 },
            [new BrokerOrderSnapshot("broker-1", "qd-local-1", "filled", 2, 100)],
            [new BrokerPositionSnapshot("SPY", 0, 2, 100)]);

        ReconciliationResult result = service.Reconcile(input);

        Assert.True(result.IsReconciled);
        Assert.Equal(SystemMode.Ready, mode.Snapshot().Mode);
    }

    [Fact]
    public void Reconcile_UnknownBrokerInstrumentFailsClosed()
    {
        var mode = new RuntimeModeState();
        var service = new ReconciliationService(mode);
        var input = new ReconciliationInput(
            new HashSet<string>(),
            new Dictionary<int, decimal>(),
            [],
            [new BrokerPositionSnapshot("UNEXPECTED", -1, 1, 2.5m)]);

        ReconciliationResult result = service.Reconcile(input);

        Assert.False(result.IsReconciled);
        Assert.Contains("UNKNOWN_BROKER_INSTRUMENT", result.MismatchCodes);
        Assert.Contains(-1, result.PositionMismatches);
    }
}
