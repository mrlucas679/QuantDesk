namespace QuantDesk.Api.PaperTrading;

public sealed record AutonomousTradingSnapshot(
    string State,
    string? Symbol,
    string? EntryOrderId,
    string? ExitOrderId,
    decimal FilledQuantity,
    string? Reason,
    decimal? GrossEdgeBps,
    decimal? EstimatedCostBps,
    decimal? NetEdgeBps,
    DateTimeOffset UpdatedAt);

public sealed class AutonomousTradingState
{
    private readonly object _gate = new();
    private AutonomousTradingSnapshot _snapshot = new("disabled", null, null, null, 0, null, null, null, null, DateTimeOffset.UtcNow);

    public AutonomousTradingSnapshot Snapshot()
    {
        lock (_gate) return _snapshot;
    }

    public void Update(string state, string? symbol = null, string? entryOrderId = null,
        string? exitOrderId = null, decimal filledQuantity = 0, string? reason = null,
        decimal? grossEdgeBps = null, decimal? estimatedCostBps = null)
    {
        lock (_gate)
        {
            _snapshot = new(state, symbol, entryOrderId, exitOrderId, filledQuantity, reason,
                grossEdgeBps, estimatedCostBps, grossEdgeBps - estimatedCostBps, DateTimeOffset.UtcNow);
        }
    }
}
