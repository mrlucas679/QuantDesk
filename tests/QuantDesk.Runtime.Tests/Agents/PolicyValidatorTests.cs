using QuantDesk.Domain.Agents;
using QuantDesk.Domain.Numerics;
using QuantDesk.Runtime.Agents;

namespace QuantDesk.Runtime.Tests.Agents;

/// <summary>
/// The boundary between an untrusted proposal and a policy the runtime will act on.
///
/// This is the control, not the system prompt. Each agent is told not to activate policy or change
/// risk, and that instruction travels over a channel an attacker can write to -- the evidence text
/// an agent reads is untrusted, and a model can be talked out of a prompt. What actually holds is
/// that the proposal is a typed record checked here against bounds, and anything outside them is
/// refused rather than clamped.
/// </summary>
public sealed class PolicyValidatorTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-02T12:00:00Z");
    private static readonly PolicyBounds Bounds =
        new(0.60, new Usd(0.50m), 0.05, 0.35, new HashSet<int> { 1, 2 });

    private const long CurrentVersion = 1;

    [Fact]
    public void ValidProposalBecomesAnExpiringLease()
    {
        var proposal = new PolicyAgentProposal(2, new HashSet<int> { 1 }, 0.70, 1m, 0.01, 0.25);

        bool valid = PolicyValidator.TryValidate(
            proposal, Bounds, Now, TimeSpan.FromHours(1), CurrentVersion,
            out TradingPolicy? policy, out string? reason);

        Assert.True(valid);
        Assert.Null(reason);
        Assert.NotNull(policy);
        Assert.Equal(Now.AddHours(1), policy.ExpiresUtc);
    }

    [Theory]
    [InlineData(0.59, 1, 0.01, 0.25, "MIN_CONFIDENCE_TOO_LOW")]
    [InlineData(0.70, 0.49, 0.01, 0.25, "MIN_EDGE_TOO_LOW")]
    [InlineData(0.70, 1, 0.06, 0.25, "EXPLORATION_OUT_OF_BOUNDS")]
    [InlineData(0.70, 1, 0.01, 0.36, "EXPERT_WEIGHT_OUT_OF_BOUNDS")]
    public void UnsafeProposalFailsClosed(
        double confidence, double edge, double exploration, double weight, string expected)
    {
        var proposal = new PolicyAgentProposal(
            2, new HashSet<int> { 1 }, confidence, (decimal)edge, exploration, weight);

        Assert.False(PolicyValidator.TryValidate(
            proposal, Bounds, Now, TimeSpan.FromHours(1), CurrentVersion,
            out TradingPolicy? policy, out string? reason));

        Assert.Null(policy);
        Assert.Equal(expected, reason);
    }

    [Fact]
    public void UnapprovedExpertFailsClosed()
    {
        var proposal = new PolicyAgentProposal(2, new HashSet<int> { 99 }, 0.70, 1m, 0.01, 0.25);

        Assert.False(PolicyValidator.TryValidate(
            proposal, Bounds, Now, TimeSpan.FromHours(1), CurrentVersion, out _, out string? reason));

        Assert.Equal("UNAPPROVED_EXPERT", reason);
    }

    [Theory]
    [InlineData(1.01)]
    [InlineData(2.0)]
    [InlineData(double.NaN)]
    public void AConfidenceThresholdOutsideItsOwnRangeIsRefused(double confidence)
    {
        // A threshold above one can never be met, so a policy carrying it silently stands the lane
        // down. It is refused as INVALID_CONTRACT rather than by a bound of its own, because the
        // proposal record already constrains confidence to [0, 1] -- and a second check here would
        // be dead code implying the contract is not trusted. This test pins where the refusal
        // comes from so removing that constraint fails loudly rather than opening a hole.
        var proposal = new PolicyAgentProposal(
            2, new HashSet<int> { 1 }, confidence, 1m, 0.01, 0.25);

        Assert.False(proposal.IsStructurallyValid());
        Assert.False(PolicyValidator.TryValidate(
            proposal, Bounds, Now, TimeSpan.FromHours(1), CurrentVersion, out _, out string? reason));

        Assert.Equal("INVALID_CONTRACT", reason);
    }

    [Fact]
    public void APolicyVersionThatDoesNotAdvanceIsRefused()
    {
        const long proposed = CurrentVersion;
        // A stale or replayed proposal presenting itself as current. Every consumer comparing
        // versions to decide which policy is newer would be comparing the wrong way round.
        var proposal = new PolicyAgentProposal(
            proposed, new HashSet<int> { 1 }, 0.70, 1m, 0.01, 0.25);

        Assert.False(PolicyValidator.TryValidate(
            proposal, Bounds, Now, TimeSpan.FromHours(1), CurrentVersion, out _, out string? reason));

        Assert.Equal("POLICY_VERSION_DID_NOT_ADVANCE", reason);
    }

    [Fact]
    public void AnOutOfBoundsProposalIsRefusedRatherThanClamped()
    {
        // Clamping would produce a policy nobody proposed and hide that something asked for twice
        // the allowed exploration. The refusal is the signal.
        var proposal = new PolicyAgentProposal(2, new HashSet<int> { 1 }, 0.70, 1m, 0.10, 0.25);

        Assert.False(PolicyValidator.TryValidate(
            proposal, Bounds, Now, TimeSpan.FromHours(1), CurrentVersion,
            out TradingPolicy? policy, out _));

        Assert.Null(policy);
    }

    [Fact]
    public void ALeaseThatNeverExpiresIsRefused()
    {
        // The point of a lease is that it runs out. A policy without an expiry is a policy change
        // nobody has to revisit.
        var proposal = new PolicyAgentProposal(2, new HashSet<int> { 1 }, 0.70, 1m, 0.01, 0.25);

        Assert.False(PolicyValidator.TryValidate(
            proposal, Bounds, Now, TimeSpan.Zero, CurrentVersion, out _, out string? reason));

        Assert.Equal("INVALID_CONTRACT", reason);
    }
}
