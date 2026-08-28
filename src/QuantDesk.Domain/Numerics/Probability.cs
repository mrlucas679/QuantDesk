namespace QuantDesk.Domain.Numerics;

public readonly record struct Probability(double Value)
{
    public static bool TryCreate(double value, out Probability result)
    {
        if (!double.IsFinite(value) || value is < 0 or > 1)
        {
            result = default;
            return false;
        }

        result = new Probability(value);
        return true;
    }
}

