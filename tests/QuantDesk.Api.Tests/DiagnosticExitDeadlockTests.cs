using QuantDesk.Api.PaperTrading;
using QuantDesk.Runtime.Time;

namespace QuantDesk.Api.Tests;

/// <summary>
/// The readiness asymmetry that lets an open position be closed.
///
/// This is a regression test for a live deadlock: a filled BTC/USD diagnostic could not be exited,
/// because "broker reconciled" means "the account is flat", and the exit path required it. Opening a
/// position therefore disqualified the system from closing it, and the exposure was stranded for as
/// long as it existed.
/// </summary>
public sealed class DiagnosticExitDeadlockTests
{
    [Fact]
    public void AnOpenPositionBlocksEntryButNeverBlocksTheExit()
    {
        var readiness = new FullSystemReadinessState(new LiveRuntimeClock());
        readiness.RecordDeterministicRuntime(true, true, true, true, true);
        readiness.RecordBrokerPreflight(reconciled: false, portfolioKnown: true, paperEndpointVerified: true);

        FullSystemReadinessSnapshot snapshot = readiness.Snapshot();

        Assert.False(snapshot.InfrastructureExecutionReady);
        Assert.True(snapshot.ExitExecutionReady);
    }

    [Fact]
    public void AFlatAccountIsReadyForBothDirections()
    {
        var readiness = new FullSystemReadinessState(new LiveRuntimeClock());
        readiness.RecordDeterministicRuntime(true, true, true, true, true);
        readiness.RecordBrokerPreflight(reconciled: true, portfolioKnown: true, paperEndpointVerified: true);

        FullSystemReadinessSnapshot snapshot = readiness.Snapshot();

        Assert.True(snapshot.InfrastructureExecutionReady);
        Assert.True(snapshot.ExitExecutionReady);
    }

    [Fact]
    public void AnUnreachableBrokerStopsTheExitToo()
    {
        // The exit gate drops only the flatness requirement. Losing broker truth entirely still stops
        // it, because an exit sized against unknown state is worse than no exit at all.
        var readiness = new FullSystemReadinessState(new LiveRuntimeClock());
        readiness.RecordDeterministicRuntime(true, true, true, true, true);
        readiness.RecordBrokerPreflight(reconciled: false, portfolioKnown: false, paperEndpointVerified: false);

        Assert.False(readiness.Snapshot().ExitExecutionReady);
    }
}
