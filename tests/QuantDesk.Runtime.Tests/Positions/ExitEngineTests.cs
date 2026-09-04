using QuantDesk.Domain.Numerics;
using QuantDesk.Domain.Strategies;
using QuantDesk.Runtime.Positions;
using QuantDesk.Runtime.Time;

namespace QuantDesk.Runtime.Tests.Positions;

public sealed class ExitEngineTests
{
    [Fact]
    public void ExitsOnInvalidatedThesisBeforeOtherRules()
    {
        var plan = new PositionManagementPlan(TimeSpan.FromMinutes(5), true, true, new Usd(10), null, "v1");
        ExitEvaluation result = new ExitEngine(new LiveRuntimeClock()).Evaluate(plan, 0, 1, new Usd(0), false, false);
        Assert.Equal(ExitReason.ThesisInvalidated, result.Reason);
    }

    [Fact]
    public void ExitsAtMaximumAdverseLoss()
    {
        var plan = new PositionManagementPlan(TimeSpan.FromMinutes(5), false, false, new Usd(10), null, "v1");
        ExitEvaluation result = new ExitEngine(new LiveRuntimeClock()).Evaluate(plan, 0, 1, new Usd(-10), true, true);
        Assert.True(result.ShouldExit);
        Assert.Equal(ExitReason.MaximumAdverseLoss, result.Reason);
    }
}
