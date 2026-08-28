namespace QuantDesk.Domain.Numerics;

public readonly record struct BasisPoints(double Value)
{
    public double Fraction => Value / 10_000.0;
}

