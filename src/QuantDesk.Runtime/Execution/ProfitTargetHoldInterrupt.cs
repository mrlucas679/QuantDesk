namespace QuantDesk.Runtime.Execution;

/// <summary>
/// Closes a position that has earned what its thesis said it would.
///
/// Why a trading system needs this at all
/// --------------------------------------
/// The exit engine had a maximum loss and a timer and nothing in between, so a position could only
/// leave by being wrong or by running out of clock. Being right bought nothing: the gain was held
/// until the timer expired and whatever the market did in the meantime was kept. On 2026-09-02
/// UNI/USD moved 9.43% while the lane held it and the lane captured 0.17% of that.
///
/// This is asymmetric in the wrong direction. A defined maximum loss caps the downside at a number
/// chosen in advance; with no target the upside is capped by nothing except when the timer happens
/// to land. The strategy's own expected move is the number that was chosen in advance for the other
/// side, and once the position has earned it the thesis is spent -- everything after that is an
/// unpaid bet that no rule authorised.
///
/// Why not a trailing stop instead
/// -------------------------------
/// A trailing stop needs a high-water mark, which needs the position marked continuously and
/// durably. The quote refresh runs once per cycle, so a mark can be minutes old, and a high-water
/// mark built from sampled quotes drifts in a way that is hard to attribute afterwards. A fixed
/// target computed from a number recorded at entry is worse in a trending market and honest about
/// what it is; the trailing version belongs with per-position marks, and so does the fix for the
/// cost estimator's overlapping windows.
///
/// A missing or unhealthy quote does not trigger an exit, for the same reason the adverse-loss
/// interrupt declines on one: acting on absent data is acting on nothing.
/// </summary>
public sealed class ProfitTargetHoldInterrupt(IHeldPositionMarker marker) : IHoldInterrupt
{
    public HoldInterrupt Evaluate(in HeldPosition position)
    {
        if (position.ProfitTarget <= 0m) return HoldInterrupt.None;
        if (position.Quantity <= 0m) return HoldInterrupt.None;
        if (position.EntryPrice is not { } entryPrice || entryPrice <= 0m) return HoldInterrupt.None;
        if (marker.CurrentMid(position.Symbol) is not { } mid || mid <= 0m) return HoldInterrupt.None;

        // Long-only, matching the adverse-loss rule: a rise above the entry price is the gain.
        decimal unrealised = (mid - entryPrice) * position.Quantity;
        if (unrealised < position.ProfitTarget) return HoldInterrupt.None;

        return HoldInterrupt.Now(
            $"ProfitTargetReached:{unrealised:0.00}>={position.ProfitTarget:0.00}");
    }
}
