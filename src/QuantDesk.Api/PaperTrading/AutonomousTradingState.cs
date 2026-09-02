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

/// <summary>
/// What the lane is doing, per instrument.
///
/// This held one snapshot for the whole lane, which was accurate only while the lane traded one
/// symbol. With several, a single slot means the last instrument evaluated overwrites the rest
/// every cycle: an operator watching the endpoint would see a position on one symbol vanish from
/// view the moment another was assessed, and could not tell "flat" from "not the most recent".
/// Keeping a snapshot per symbol makes the lane's actual state observable.
/// </summary>
public sealed class AutonomousTradingState
{
    private readonly object _gate = new();
    private readonly Dictionary<string, AutonomousTradingSnapshot> _bySymbol =
        new(StringComparer.OrdinalIgnoreCase);
    private AutonomousTradingSnapshot _lane =
        new("disabled", null, null, null, 0, null, null, null, null, DateTimeOffset.UtcNow);

    /// <summary>
    /// The lane-wide view, kept for callers that predate multi-symbol and for lifecycle states that
    /// genuinely belong to the lane rather than an instrument.
    /// </summary>
    public AutonomousTradingSnapshot Snapshot()
    {
        lock (_gate)
        {
            // Prefer whichever symbol is doing something, so a single-slot reader is not shown an
            // abstention while another instrument is actually holding a position. Failing that,
            // the most recently evaluated symbol -- never the lane record, which only carries
            // states no instrument owns and would otherwise report "disabled" over a live lane
            // that had simply abstained on every symbol.
            return _bySymbol.Values.FirstOrDefault(IsWorking)
                ?? _bySymbol.Values.OrderByDescending(item => item.UpdatedAt).FirstOrDefault()
                ?? _lane;
        }
    }

    /// <summary>Every instrument's current state, ordered for stable display.</summary>
    public IReadOnlyList<AutonomousTradingSnapshot> SnapshotAll()
    {
        lock (_gate)
        {
            return _bySymbol.Count == 0
                ? [_lane]
                : [.. _bySymbol.Values.OrderBy(item => item.Symbol, StringComparer.OrdinalIgnoreCase)];
        }
    }

    /// <summary>Records a lane-wide state that no single instrument owns.</summary>
    public void Update(string state, string? symbol = null, string? entryOrderId = null,
        string? exitOrderId = null, decimal filledQuantity = 0, string? reason = null,
        decimal? grossEdgeBps = null, decimal? estimatedCostBps = null)
    {
        lock (_gate)
        {
            _lane = Build(state, symbol, entryOrderId, exitOrderId, filledQuantity, reason,
                grossEdgeBps, estimatedCostBps);
        }
    }

    /// <summary>Records the state of one instrument.</summary>
    public void UpdateSymbol(string instrument, string state, string? symbol = null,
        string? entryOrderId = null, string? exitOrderId = null, decimal filledQuantity = 0,
        string? reason = null, decimal? grossEdgeBps = null, decimal? estimatedCostBps = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instrument);
        lock (_gate)
        {
            _bySymbol[instrument] = Build(state, symbol ?? instrument, entryOrderId, exitOrderId,
                filledQuantity, reason, grossEdgeBps, estimatedCostBps);
        }
    }

    private static AutonomousTradingSnapshot Build(
        string state, string? symbol, string? entryOrderId, string? exitOrderId,
        decimal filledQuantity, string? reason, decimal? grossEdgeBps, decimal? estimatedCostBps) =>
        new(state, symbol, entryOrderId, exitOrderId, filledQuantity, reason,
            grossEdgeBps, estimatedCostBps, grossEdgeBps - estimatedCostBps, DateTimeOffset.UtcNow);

    private static bool IsWorking(AutonomousTradingSnapshot snapshot) =>
        snapshot.State is "holding" or "submitting_entry" or "submitting_exit";
}
