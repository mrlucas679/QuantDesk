using QuantDesk.Domain.Numerics;
using QuantDesk.Domain.Strategies;
using QuantDesk.Runtime.Time;

namespace QuantDesk.Runtime.Positions;

public enum ExitReason
{
    None,
    Expired,
    ThesisInvalidated,
    RegimeChanged,
    MaximumAdverseLoss
}

public readonly record struct ExitEvaluation(bool ShouldExit, ExitReason Reason, string PolicyVersion);

/// <summary>
/// Decides whether an open position should be closed, and says which rule closed it.
///
/// The holding period is converted through the clock rather than through
/// <c>Stopwatch.Frequency</c>. It used to be the latter, which is right only when the timestamps
/// being compared also came from a live Stopwatch -- so under a virtual clock a five-minute maximum
/// hold became five hundred minutes of virtual time on Linux, and every test exercising expiry with
/// that clock was passing for the wrong reason.
/// </summary>
public sealed class ExitEngine(IRuntimeClock clock)
{
    public ExitEvaluation Evaluate(
        PositionManagementPlan plan,
        long openedMonotonicTicks,
        long nowMonotonicTicks,
        Usd unrealizedPnl,
        bool thesisValid,
        bool regimeValid)
    {
        if (nowMonotonicTicks < openedMonotonicTicks)
            throw new ArgumentOutOfRangeException(nameof(nowMonotonicTicks));
        if (plan.ExitOnThesisInvalidation && !thesisValid)
            return new(true, ExitReason.ThesisInvalidated, plan.ExitPolicyVersion);
        if (plan.ExitOnRegimeChange && !regimeValid)
            return new(true, ExitReason.RegimeChanged, plan.ExitPolicyVersion);
        if (plan.MaximumAdverseLoss is Usd loss && unrealizedPnl.Value <= -loss.Value)
            return new(true, ExitReason.MaximumAdverseLoss, plan.ExitPolicyVersion);
        long holdingTicks = clock.MonotonicTicksFor(plan.MaximumHoldingPeriod);
        if (holdingTicks > 0 && nowMonotonicTicks - openedMonotonicTicks >= holdingTicks)
            return new(true, ExitReason.Expired, plan.ExitPolicyVersion);
        return new(false, ExitReason.None, plan.ExitPolicyVersion);
    }
}
