namespace QuantDesk.Runtime.Allocator;

public static class BoundedWeightProjector
{
    public static void NormalizeWithCap(Span<double> weights, int count, double maxWeight)
    {
        if (count <= 0) return;
        if (count > weights.Length) throw new ArgumentOutOfRangeException(nameof(count));
        if (maxWeight <= 0 || maxWeight * count < 1.0 - 1e-12)
            throw new ArgumentOutOfRangeException(nameof(maxWeight));
        for (int index = 0; index < count; index++)
            if (!double.IsFinite(weights[index]) || weights[index] < 0)
                throw new ArgumentException("Weights must be finite and non-negative.", nameof(weights));

        Span<bool> fixedMask = stackalloc bool[count];
        while (true)
        {
            double fixedMass = 0;
            double freeMass = 0;
            int freeCount = 0;
            for (int index = 0; index < count; index++)
            {
                if (fixedMask[index]) fixedMass += weights[index];
                else { freeMass += weights[index]; freeCount++; }
            }

            if (freeCount == 0) break;
            double targetFreeMass = 1.0 - fixedMass;
            if (freeMass <= 0)
            {
                double equal = targetFreeMass / freeCount;
                for (int index = 0; index < count; index++)
                    if (!fixedMask[index]) weights[index] = equal;
            }
            else
            {
                double scale = targetFreeMass / freeMass;
                for (int index = 0; index < count; index++)
                    if (!fixedMask[index]) weights[index] *= scale;
            }

            bool changed = false;
            for (int index = 0; index < count; index++)
            {
                if (!fixedMask[index] && weights[index] > maxWeight)
                {
                    weights[index] = maxWeight;
                    fixedMask[index] = true;
                    changed = true;
                }
            }

            if (!changed) break;
        }

        double sum = 0;
        for (int index = 0; index < count; index++) sum += weights[index];
        if (Math.Abs(sum - 1.0) > 1e-8) throw new InvalidOperationException("Weight projection failed.");
    }
}
