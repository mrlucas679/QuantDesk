namespace QuantDesk.Domain.Runtime;

public enum DataQuality
{
    Healthy,
    Degraded,
    Stale,
    Invalid,
    Disconnected,
    GapDetected
}
