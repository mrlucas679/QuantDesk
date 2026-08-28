namespace QuantDesk.Runtime.Sequencing;

public sealed class SequenceGenerator
{
    private long _current;

    public long Next() => Interlocked.Increment(ref _current);
}

