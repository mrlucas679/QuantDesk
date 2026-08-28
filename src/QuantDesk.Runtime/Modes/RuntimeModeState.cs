using QuantDesk.Domain.Runtime;

namespace QuantDesk.Runtime.Modes;

public sealed class RuntimeModeState
{
    private readonly Lock _gate = new();
    private SystemMode _mode = SystemMode.Booting;
    private string? _reason;

    public (SystemMode Mode, string? Reason) Snapshot()
    {
        lock (_gate)
        {
            return (_mode, _reason);
        }
    }

    public void Transition(SystemMode mode, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        lock (_gate)
        {
            _mode = mode;
            _reason = reason.Trim();
        }
    }
}
