using QuantDesk.Domain.Numerics;
using QuantDesk.Domain.Strategies;

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

public sealed class ExitEngine
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
        long holdingTicks = (long)(plan.MaximumHoldingPeriod.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);
        if (holdingTicks > 0 && nowMonotonicTicks - openedMonotonicTicks >= holdingTicks)
            return new(true, ExitReason.Expired, plan.ExitPolicyVersion);
        return new(false, ExitReason.None, plan.ExitPolicyVersion);
    }
}
