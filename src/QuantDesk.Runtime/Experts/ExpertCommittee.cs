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

        // Magnitude against the floor, not the signed return.
        //
        // This read `weightedReturn >= minimumExpectedReturnBps`, so a bearish forecast could never
        // be actionable however strong it was: a committee expecting -51 bps failed a +1 bps floor
        // by construction and was reported as disagreement, which is not what happened -- the
        // experts agreed, emphatically, that the price was going down.
        //
        // It was the last long-only assumption in the decision path, and the most expensive kind:
        // the rules learned to say Short, execution learned to sell, the compiler learned to carry
        // direction, and every one of those shorts still died here on a comparison. On 2026-09-04
        // it refused three equity rules firing short on SPY, QQQ and IWM with a measured record
        // behind them.
        //
        // The floor asks "is the expected move big enough to be worth a round trip", and that
        // question is about size. Direction is carried by the sign and read downstream, where the
        // compiler turns a negative expected return into a short and prices its gross P&L on the
        // magnitude. Mechanism conflict is already refused above, so a near-zero average produced by
        // experts pulling opposite ways cannot reach this line and be rescued by an absolute value.
        bool actionable = agreement >= minimumAgreementScore
            && Math.Abs(weightedReturn) >= minimumExpectedReturnBps;
        return new CommitteeDecision(instrumentSlot, weightedReturn, agreement, actionable,
            actionable ? "consensus" : "committee_disagreement", supporting)
        {
            Verdict = actionable ? CommitteeVerdict.Consensus : CommitteeVerdict.Abstain
        };
    }

    private static CommitteeDecision Abstain(int slot, string reason) =>
        new(slot, 0, 0, false, reason, []) { Verdict = CommitteeVerdict.Abstain };
}
