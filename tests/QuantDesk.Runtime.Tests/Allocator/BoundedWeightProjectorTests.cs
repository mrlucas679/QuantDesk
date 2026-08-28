using QuantDesk.Runtime.Allocator;

namespace QuantDesk.Runtime.Tests.Allocator;

public sealed class BoundedWeightProjectorTests
{
    [Theory]
    [InlineData(3, 0.6)]
    [InlineData(5, 0.4)]
    public void NormalizeWithCap_ProducesBoundedSimplex(int count, double cap)
    {
        double[] weights = Enumerable.Repeat(1.0, count).ToArray();

        BoundedWeightProjector.NormalizeWithCap(weights, count, cap);

        Assert.Equal(1.0, weights.Sum(), 8);
        Assert.All(weights, weight => Assert.InRange(weight, 0, cap));
    }

    [Fact]
    public void NormalizeWithCap_RejectsImpossibleCap()
    {
        double[] weights = [1, 1, 1];

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BoundedWeightProjector.NormalizeWithCap(weights, 3, 0.2));
    }
}

