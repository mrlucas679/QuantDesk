using QuantDesk.Domain.Execution;

namespace QuantDesk.Runtime.Execution;

/// <summary>
/// The part of a held position that any early-exit rule needs, whatever lifecycle owns it.
///
/// Spot and multi-leg keep very different records — one has a symbol and a quantity, the other has
/// legs, an entry command, and an expiry — but every reason to close early asks the same few
/// questions: what is it, how much did we pay, how much may it lose, what licensed it, and when do
/// the contracts stop existing. Projecting both records onto this view means one set of rules
/// applies to both lanes, rather than the options lane quietly getting a weaker version.
/// </summary>
/// <param name="ExecutionId">Identifies the execution in operator-facing reasons.</param>
/// <param name="Symbol">Underlying or spot instrument, for quoting.</param>
/// <param name="Quantity">Filled quantity being held.</param>
/// <param name="EntryPrice">Average entry price, or null before a fill.</param>
/// <param name="DefinedMaximumLoss">The most this position was authorised to lose.</param>
/// <param name="Ownership">The publication that licensed it, or null when none did.</param>
/// <param name="EarliestLegExpiry">
/// When the nearest-dated contract expires, or null for an instrument that does not expire. Options
/// held toward expiry behave nothing like the same position with weeks to run, so a rule that only
/// watched price would miss the risk entirely.
/// </param>
/// <param name="MinimumDaysToExpiry">
/// How close to expiry this specific position was authorised to be held, from its management plan,
/// or null to fall back to the lane's configured floor. Per-position because a wide spread and a
/// tight one do not become dangerous at the same distance from expiry.
/// </param>
public readonly record struct HeldPosition(
    string ExecutionId,
    string Symbol,
    decimal Quantity,
    decimal? EntryPrice,
    decimal DefinedMaximumLoss,
    PositionOwnership? Ownership,
    DateTimeOffset? EarliestLegExpiry,
    int? MinimumDaysToExpiry = null,
    decimal ProfitTarget = 0m,
    decimal? SellableQuantity = null,
    decimal ExitCostRate = 0m,
    string? StrategyId = null)
{
    /// <summary>
    /// What closing this position would actually realise at <paramref name="mid"/>.
    ///
    /// Two corrections against the naive (mid - entry) * quantity, and both were live.
    ///
    /// The account does not hold the quantity that was bought. Alpaca charges its spot crypto fee
    /// in kind, so an entry that filled 28.02521709 leaves 27.955154 to sell -- 25 bps taken off
    /// the top, measured from delivered quantity across 62 round trips on 2026-09-02. Marking the
    /// filled quantity overstates the position by that much in both directions.
    ///
    /// And the exit has not been paid for. A position sitting exactly on its profit target has not
    /// earned it: closing costs another 25 bps in kind, so it realises a quarter of a percent less
    /// than the mark suggests. Symmetrically, a position at exactly its defined maximum loss will
    /// realise more than that maximum once the exit is paid -- which makes a bound that sizes the
    /// capital reservation quietly wrong in the one direction that matters.
    /// </summary>
    /// <param name="mid">The current mid, or null when no healthy quote exists.</param>
    public decimal? RealisableProfit(decimal? mid)
    {
        if (mid is not { } price || price <= 0m) return null;
        if (EntryPrice is not { } entry || entry <= 0m) return null;

        decimal held = SellableQuantity ?? Quantity;
        if (held <= 0m) return null;

        decimal proceeds = price * held * (1m - ExitCostRate);
        return proceeds - (entry * Quantity);
    }
}
