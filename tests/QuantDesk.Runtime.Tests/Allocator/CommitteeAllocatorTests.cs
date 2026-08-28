using QuantDesk.Domain.Experts;
using QuantDesk.Runtime.Allocator;

namespace QuantDesk.Runtime.Tests.Allocator;

public sealed class CommitteeAllocatorTests
{
    [Fact]
    public void AllocatesOnlyActionableDecisionsUnderCap()
    {
        var allocator = new CommitteeAllocator(.7);
        var decisions = new[]
        {
            new CommitteeDecision(1, 10, .9, true, "consensus", [1]),
            new CommitteeDecision(2, 5, .8, true, "consensus", [2]),
            new CommitteeDecision(3, 20, .9, false, "committee_disagreement", [])
        };
        IReadOnlyDictionary<int, double> result = allocator.Allocate(decisions);
        Assert.Equal(2, result.Count);
        Assert.InRange(result[1], 0, .7);
        Assert.InRange(result[2], 0, .7);
        Assert.InRange(result.Values.Sum(), .999999, 1.000001);
    }
}
