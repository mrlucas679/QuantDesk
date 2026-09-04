using QuantDesk.Domain.Agents;
using QuantDesk.Domain.Numerics;

namespace QuantDesk.Runtime.Agents;

/// <summary>
/// Converts an untrusted agent proposal into a bounded, expiring policy lease.
///
/// This is the control, not the prompt. The system prompts tell each agent not to activate policy
/// or change risk, and that instruction is defence in depth over a channel an attacker can write
/// to -- the evidence text an agent reads is untrusted, and a model can be talked out of a prompt.
/// What actually holds is that the proposal is a typed record checked against bounds here, and
/// anything outside them is refused rather than clamped.
///
/// Refused rather than clamped, deliberately. Clamping a proposal that asked for twice the allowed
/// exploration produces a policy nobody proposed and hides that something asked for it.
/// </summary>
public static class PolicyValidator
{
    public static bool TryValidate(
        PolicyAgentProposal proposal,
        PolicyBounds bounds,
        DateTimeOffset now,
        TimeSpan lease,
        long currentPolicyVersion,
        out TradingPolicy? policy,
        out string? reason)
    {
        policy = null;
        if (!proposal.IsStructurallyValid() || !bounds.IsValid() || lease <= TimeSpan.Zero)
            return Fail("INVALID_CONTRACT", out reason);
        // Only the floor is checked here. A confidence above one -- which could never be met, so a
        // policy carrying it would silently stand the lane down -- is already refused by
        // PolicyAgentProposal.IsStructurallyValid, which bounds it to [0, 1]. Checking it twice
        // would imply the contract is not trusted, and the second check would be dead.
        if (proposal.MinimumConfidence < bounds.MinimumConfidenceFloor)
            return Fail("MIN_CONFIDENCE_TOO_LOW", out reason);
        if (proposal.MinimumNetEdgeUsd < bounds.MinimumNetEdgeFloor.Value)
            return Fail("MIN_EDGE_TOO_LOW", out reason);
        if (proposal.ExplorationFraction is < 0 ||
            proposal.ExplorationFraction > bounds.MaximumExplorationFraction)
            return Fail("EXPLORATION_OUT_OF_BOUNDS", out reason);
        if (proposal.MaximumExpertWeight is <= 0 ||
            proposal.MaximumExpertWeight > bounds.MaximumExpertWeightCeiling)
            return Fail("EXPERT_WEIGHT_OUT_OF_BOUNDS", out reason);
        if (!proposal.EnabledExperts.IsSubsetOf(bounds.AllowedExperts))
            return Fail("UNAPPROVED_EXPERT", out reason);

        // A policy version that does not advance lets a replayed or stale proposal present itself
        // as current, and every consumer comparing versions to decide which policy is newer would
        // be comparing the wrong way round.
        if (proposal.PolicyVersion <= currentPolicyVersion)
            return Fail("POLICY_VERSION_DID_NOT_ADVANCE", out reason);

        policy = new TradingPolicy(
            proposal.PolicyVersion, now, now.Add(lease), proposal.EnabledExperts,
            proposal.MinimumConfidence, new Usd(proposal.MinimumNetEdgeUsd),
            proposal.ExplorationFraction, proposal.MaximumExpertWeight);
        reason = null;
        return true;
    }

    private static bool Fail(string failure, out string? reason)
    {
        reason = failure;
        return false;
    }
}
