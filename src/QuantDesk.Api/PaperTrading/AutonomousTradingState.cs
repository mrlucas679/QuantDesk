namespace QuantDesk.Api.PaperTrading;

public sealed record AutonomousTradingSnapshot(
    string State,
    string? Symbol,
    string? EntryOrderId,
    string? ExitOrderId,
    decimal FilledQuantity,
    string? Reason,
    DateTimeOffset UpdatedAt);

public sealed class AutonomousTradingState
{
    private readonly object _gate = new();
    private AutonomousTradingSnapshot _snapshot = new("disabled", null, null, null, 0, null, DateTimeOffset.UtcNow);

    public AutonomousTradingSnapshot Snapshot()
    {
        lock (_gate) return _snapshot;
    }

    public void Update(string state, string? symbol = null, string? entryOrderId = null,
        string? exitOrderId = null, decimal filledQuantity = 0, string? reason = null)
    {
        lock (_gate)
        {
            _snapshot = new(state, symbol, entryOrderId, exitOrderId, filledQuantity, reason, DateTimeOffset.UtcNow);
        }
    }
}
