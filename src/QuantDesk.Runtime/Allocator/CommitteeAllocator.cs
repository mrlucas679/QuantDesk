using QuantDesk.Domain.Experts;

namespace QuantDesk.Runtime.Allocator;

public sealed class CommitteeAllocator(double maxWeight)
{
    public IReadOnlyDictionary<int, double> Allocate(IReadOnlyList<CommitteeDecision> decisions)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        var actionable = decisions.Where(decision => decision.Actionable && decision.ExpectedReturnBps > 0).ToArray();
        if (actionable.Length == 0) return new Dictionary<int, double>();
        double[] weights = actionable.Select(decision => decision.ExpectedReturnBps * Math.Max(decision.AgreementScore, 0)).ToArray();
        BoundedWeightProjector.NormalizeWithCap(weights, weights.Length, maxWeight);
        return actionable.Select((decision, index) => new { decision.InstrumentSlot, Weight = weights[index] })
            .ToDictionary(item => item.InstrumentSlot, item => item.Weight);
    }
}
