using QuantDesk.Runtime.Persistence;

namespace QuantDesk.Runtime.Execution;

/// <summary>Supplies the current mid for a held symbol, or null when no healthy quote exists.</summary>
public interface IHeldPositionMarker
{
    decimal? CurrentMid(string symbol);
}

/// <summary>
/// Closes a position whose unrealised loss has reached the maximum it was authorised to lose.
///
/// <c>DefinedMaximumLoss</c> was already computed, already persisted, and already used to size the
/// capital reservation — it was simply never compared against anything. A position could therefore
/// lose several multiples of its stated maximum and keep running, because the only thing that ended
/// a hold was the clock.
///
/// A missing or unhealthy quote does not trigger an exit. That is deliberate: firing on the absence
/// of data would close positions during a feed outage, when the account is least able to judge the
/// price it would get. The scheduled time still bounds the hold in that case.
/// </summary>
public sealed class AdverseLossHoldInterrupt(IHeldPositionMarker marker) : IHoldInterrupt
{
    public HoldInterrupt Evaluate(SpotExecutionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (record.DefinedMaximumLoss <= 0m) return HoldInterrupt.None;
        if (record.EntryFilledQuantity <= 0m) return HoldInterrupt.None;
        if (record.EntryAverageFillPrice is not { } entryPrice || entryPrice <= 0m) return HoldInterrupt.None;
        if (marker.CurrentMid(record.Symbol) is not { } mid || mid <= 0m) return HoldInterrupt.None;

        // Long-only spot: a fall below the entry price is the loss.
        decimal unrealised = (mid - entryPrice) * record.EntryFilledQuantity;
        if (unrealised > -record.DefinedMaximumLoss) return HoldInterrupt.None;

        return HoldInterrupt.Now(
            $"AdverseLossBreached:{unrealised:0.00}<=-{record.DefinedMaximumLoss:0.00}");
    }
}
