using QuantDesk.Domain.Numerics;

namespace QuantDesk.Domain.Tests.Numerics;

public sealed class ProbabilityTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(0.5)]
    [InlineData(1)]
    public void TryCreate_AcceptsClosedUnitInterval(double value)
    {
        bool created = Probability.TryCreate(value, out Probability probability);

        Assert.True(created);
        Assert.Equal(value, probability.Value);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void TryCreate_RejectsInvalidValues(double value)
    {
        Assert.False(Probability.TryCreate(value, out _));
    }
}

