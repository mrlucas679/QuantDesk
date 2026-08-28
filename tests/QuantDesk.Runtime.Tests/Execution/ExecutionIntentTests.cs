using QuantDesk.Domain.Execution;
using QuantDesk.Runtime.Execution;

namespace QuantDesk.Runtime.Tests.Execution;

public sealed class ExecutionIntentTests
{
    [Fact]
    public void Intent_EnforcesApprovedReservedQueuedSequence()
    {
        var intent = new ExecutionIntent(1, 2, "trend");

        intent.TransitionTo(ExecutionIntentState.Approved);
        intent.AttachApproval("qd-campaign-trend-spy-1", 10, 10);
        intent.TransitionTo(ExecutionIntentState.Queued);

        Assert.Equal(ExecutionIntentState.Queued, intent.State);
        Assert.Equal("qd-campaign-trend-spy-1", intent.ClientOrderId);
    }

    [Fact]
    public void Intent_RejectsBrokerSubmissionWithoutApprovalAndReservation()
    {
        var intent = new ExecutionIntent(1, 2, "trend");

        Assert.Throws<InvalidOperationException>(() => intent.TransitionTo(ExecutionIntentState.Submitted));
        Assert.Equal(ExecutionIntentState.Created, intent.State);
    }

    [Fact]
    public void Intent_UnknownBrokerTruthTransitionsToReconciliation()
    {
        var intent = new ExecutionIntent(1, 2, "trend");
        intent.TransitionTo(ExecutionIntentState.Approved);
        intent.AttachApproval("qd-campaign-trend-spy-1", 10, 10);
        intent.TransitionTo(ExecutionIntentState.Queued);
        intent.TransitionTo(ExecutionIntentState.Submitted);

        intent.TransitionTo(ExecutionIntentState.Reconciling);

        Assert.Equal(ExecutionIntentState.Reconciling, intent.State);
    }
}

