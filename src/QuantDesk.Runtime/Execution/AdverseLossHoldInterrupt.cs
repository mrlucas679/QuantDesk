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
    public HoldInterrupt Evaluate(in HeldPosition position)
    {
        if (position.DefinedMaximumLoss <= 0m) return HoldInterrupt.None;
        if (position.Quantity <= 0m) return HoldInterrupt.None;

        // What closing would actually realise: the quantity the account holds after the venue's
        // in-kind entry fee, less the in-kind fee the exit will cost. Marking the filled quantity
        // gross of the exit meant a position at exactly its defined maximum loss realised more than
        // that maximum -- a bound that sizes the capital reservation, breached by construction.
        if (position.RealisableProfit(marker.CurrentMid(position.Symbol)) is not { } unrealised)
            return HoldInterrupt.None;

        if (unrealised > -position.DefinedMaximumLoss) return HoldInterrupt.None;

        return HoldInterrupt.Now(
            $"AdverseLossBreached:{unrealised:0.00}<=-{position.DefinedMaximumLoss:0.00}");
    }
}
