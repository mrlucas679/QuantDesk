namespace QuantDesk.Runtime.Features;

public static class FeatureCalculations
{
    public static FeatureValue TimeBasedLogReturn(
        TimestampedRingBuffer<PriceSample> samples,
        long nowNanoseconds,
        long horizonNanoseconds)
    {
        if (samples.Count < 2 || horizonNanoseconds <= 0) return default;

        PriceSample latest = samples.GetFromNewest(0);
        long target = nowNanoseconds - horizonNanoseconds;

        for (int index = 1; index < samples.Count; index++)
        {
            PriceSample sample = samples.GetFromNewest(index);
            if (sample.EventUnixNanoseconds > target) continue;

            if (sample.Price <= 0 || latest.Price <= 0) return default;

            return new FeatureValue(
                Math.Log(latest.Price / sample.Price),
                true,
                samples.Count);
        }

        return new FeatureValue(0, false, samples.Count);
    }
}

