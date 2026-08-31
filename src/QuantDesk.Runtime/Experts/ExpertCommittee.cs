using QuantDesk.Domain.Experts;
using QuantDesk.Domain.Forecasts;

namespace QuantDesk.Runtime.Experts;

public sealed class ExpertCommittee(double minimumAgreementScore, double minimumExpectedReturnBps)
{
    public CommitteeDecision Evaluate(
        int instrumentSlot,
        ReadOnlySpan<ExpertVote> votes,
        long nowMonotonicTicks,
        long sourceStateVersion)
    {
        if (votes.IsEmpty) return Abstain(instrumentSlot, "no_experts");
        double totalWeight = 0;
        double weightedReturn = 0;
        double agreement = 0;
        bool hasPositiveMechanism = false;
        bool hasNegativeMechanism = false;
        List<int> supporting = [];
        for (int index = 0; index < votes.Length; index++)
        {
            ExpertVote vote = votes[index];
            if (vote.ExpertId < 0 || !double.IsFinite(vote.Weight) || vote.Weight <= 0 ||
                vote.Forecast.Metadata.InstrumentSlot != instrumentSlot ||
                !ForecastValidity.IsFresh(vote.Forecast.Metadata, nowMonotonicTicks) ||
                !ForecastValidity.IsCausal(vote.Forecast.Metadata, sourceStateVersion))
                continue;
            totalWeight += vote.Weight;
            weightedReturn += vote.Weight * vote.Forecast.ExpectedReturnBps;
            hasPositiveMechanism |= vote.Forecast.ExpectedReturnBps > 0;
            hasNegativeMechanism |= vote.Forecast.ExpectedReturnBps < 0;
            agreement += vote.Weight * Math.Clamp(vote.Forecast.CalibrationScore, 0, 1);
            supporting.Add(vote.ExpertId);
        }
        if (totalWeight <= 0) return Abstain(instrumentSlot, "insufficient_valid_evidence");
        if (hasPositiveMechanism && hasNegativeMechanism)
        {
            // Contradiction is evidence of uncertainty, not a weak direction that can be
            // averaged into an entry signal.
            return new CommitteeDecision(instrumentSlot, 0, 0, false,
                "mechanism_conflict", supporting) { Verdict = CommitteeVerdict.Uncertain };
        }
        weightedReturn /= totalWeight;
        agreement /= totalWeight;
        bool actionable = agreement >= minimumAgreementScore && weightedReturn >= minimumExpectedReturnBps;
        return new CommitteeDecision(instrumentSlot, weightedReturn, agreement, actionable,
            actionable ? "consensus" : "committee_disagreement", supporting)
        {
            Verdict = actionable ? CommitteeVerdict.Consensus : CommitteeVerdict.Abstain
        };
    }

    private static CommitteeDecision Abstain(int slot, string reason) =>
        new(slot, 0, 0, false, reason, []) { Verdict = CommitteeVerdict.Abstain };
}
