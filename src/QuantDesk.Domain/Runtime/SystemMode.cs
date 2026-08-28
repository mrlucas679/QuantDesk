namespace QuantDesk.Domain.Runtime;

public enum SystemMode
{
    Booting,
    Preflight,
    Warming,
    Syncing,
    Ready,
    Degraded,
    EntryHalted,
    RiskReductionOnly,
    Emergency,
    Shutdown
}

