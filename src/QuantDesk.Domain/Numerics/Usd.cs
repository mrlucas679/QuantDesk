namespace QuantDesk.Domain.Numerics;

public readonly record struct Usd(decimal Value)
{
    public static Usd Zero => new(0m);

    public static Usd operator +(Usd left, Usd right) => new(left.Value + right.Value);

    public static Usd operator -(Usd left, Usd right) => new(left.Value - right.Value);

    public static Usd operator *(Usd value, decimal multiplier) => new(value.Value * multiplier);

    public static Usd operator /(Usd value, decimal divisor) => new(value.Value / divisor);

    public static bool operator >(Usd left, Usd right) => left.Value > right.Value;

    public static bool operator <(Usd left, Usd right) => left.Value < right.Value;

    public static bool operator >=(Usd left, Usd right) => left.Value >= right.Value;

    public static bool operator <=(Usd left, Usd right) => left.Value <= right.Value;
}

