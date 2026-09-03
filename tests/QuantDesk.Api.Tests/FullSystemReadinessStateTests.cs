using QuantDesk.Api.PaperTrading;
using QuantDesk.Runtime.Time;

namespace QuantDesk.Api.Tests;

public sealed class FullSystemReadinessStateTests
{
    [Fact]
    public void BrokerConnectivityAloneDoesNotDeclareFullSystemReady()
    {
        var state = new FullSystemReadinessState(new LiveRuntimeClock());

        state.RecordBrokerPreflight(true, true, true);

        Assert.False(state.Snapshot().Ready);
    }

    [Fact]
    public void EveryIndependentGateMustPassBeforeReady()
    {
        var state = new FullSystemReadinessState(new LiveRuntimeClock());
        state.RecordBrokerPreflight(true, true, true);
        state.RecordDeterministicRuntime(true, true, true, true, true);
        state.RecordResearchPlane(true, true);
        state.RecordStreams(true, true);

        Assert.True(state.Snapshot().Ready);
    }
}
