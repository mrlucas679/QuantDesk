namespace QuantDesk.Runtime.Features;

public readonly record struct FeatureValue(
    double Value,
    bool IsValid,
    int ObservationCount);

public readonly record struct PriceSample(
    long EventUnixNanoseconds,
    double Price);

public sealed class TimestampedRingBuffer<T>
{
    private readonly T[] _values;
    private int _cursor;
    private int _count;

    public TimestampedRingBuffer(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _values = new T[capacity];
    }

    public int Count => _count;

    public void Add(T value)
    {
        _values[_cursor] = value;
        _cursor = (_cursor + 1) % _values.Length;
        if (_count < _values.Length) _count++;
    }

    public T GetFromNewest(int offset)
    {
        if ((uint)offset >= (uint)_count)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        int index = (_cursor - 1 - offset + _values.Length) % _values.Length;
        return _values[index];
    }
}

public sealed class EwmaVariance
{
    private readonly double _lambda;
    private bool _initialized;

    public EwmaVariance(double lambda)
    {
        if (lambda is <= 0 or >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(lambda));
        }

        _lambda = lambda;
    }

    public double Value { get; private set; }

    public bool TryPush(double logReturn)
    {
        if (!double.IsFinite(logReturn)) return false;

        double squared = logReturn * logReturn;
        Value = _initialized ? (_lambda * Value) + ((1.0 - _lambda) * squared) : squared;
        _initialized = true;
        return true;
    }
}

