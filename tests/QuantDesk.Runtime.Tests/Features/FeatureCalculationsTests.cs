using QuantDesk.Runtime.Features;

namespace QuantDesk.Runtime.Tests.Features;

public sealed class FeatureCalculationsTests
{
    [Fact]
    public void TimeBasedLogReturn_UsesTimestampHorizonNotSampleCount()
    {
        var samples = new TimestampedRingBuffer<PriceSample>(8);
        samples.Add(new PriceSample(0, 100));
        samples.Add(new PriceSample(240_000_000_000, 105));
        samples.Add(new PriceSample(300_000_000_000, 110));

        FeatureValue result = FeatureCalculations.TimeBasedLogReturn(
            samples,
            300_000_000_000,
            300_000_000_000);

        Assert.True(result.IsValid);
        Assert.Equal(Math.Log(1.1), result.Value, 12);
    }

    [Fact]
    public void TimeBasedLogReturn_ReportsNotReadyInsteadOfInventingZero()
    {
        var samples = new TimestampedRingBuffer<PriceSample>(8);
        samples.Add(new PriceSample(300_000_000_000, 110));

        FeatureValue result = FeatureCalculations.TimeBasedLogReturn(
            samples,
            300_000_000_000,
            300_000_000_000);

        Assert.False(result.IsValid);
    }
}

