using QuantDesk.Domain.Execution;

namespace QuantDesk.Api.PaperTrading;

/// <summary>Exposure that one durable lane record accounts for.</summary>
/// <param name="Symbol">The instrument the record trades, in whatever form that lane records it.</param>
/// <param name="ClientOrderIds">Every deterministic client order ID the record owns.</param>
public sealed record ExposureClaim(string Symbol, IReadOnlyCollection<string> ClientOrderIds);

/// <summary>
/// A lane that can state which broker exposure belongs to it, derived from its durable store.
/// </summary>
public interface IExposureClaimSource
{
    /// <summary>Names the lane in operator-facing messages.</summary>
    string LaneName { get; }

    /// <summary>Claims from every record that has not reached a terminal state.</summary>
    IReadOnlyList<ExposureClaim> ListClaims();
}

/// <summary>
/// What the system can and cannot account for at the broker.
/// </summary>
public sealed record BrokerExposureAttribution(
    IReadOnlyList<BrokerOrderSnapshot> UnattributedOrders,
    IReadOnlyList<BrokerPositionSnapshot> UnattributedPositions,
    IReadOnlyList<string> ClaimedSymbols)
{
    /// <summary>Exposure no lane claims. The only kind that must halt entry everywhere.</summary>
    public bool HasUnattributedExposure =>
        UnattributedOrders.Count > 0 || UnattributedPositions.Count > 0;

    /// <summary>True when some lane already holds or is working this instrument.</summary>
    public bool IsClaimed(string symbol) =>
        ClaimedSymbols.Any(claimed => BrokerSymbol.Matches(claimed, symbol));

    /// <summary>A short operator-readable summary of what could not be attributed.</summary>
    public string Describe() => HasUnattributedExposure
        ? $"{UnattributedOrders.Count} unattributed order(s), " +
          $"{UnattributedPositions.Count} unattributed position(s): " +
          string.Join(", ", UnattributedPositions.Select(position => position.Symbol)
              .Concat(UnattributedOrders.Select(order => order.ClientOrderId))
              .Take(5))
        : "all broker exposure is attributed";
}

/// <summary>
/// Decides which broker orders and positions this system created.
///
/// This is what makes a narrower entry gate safe. Halting on *any* broker position was correct but
/// blunt: it stopped a lane from trading because some other lane held an unrelated instrument, and it
/// could not distinguish that from genuinely foreign exposure — the case the halt exists for. Without
/// attribution the only safe rule was to refuse everything.
///
/// Orders attribute exactly, by deterministic client order ID. Positions do not carry one, so they are
/// attributed by symbol against the lanes' nonterminal records. That asymmetry is deliberate and worth
/// stating plainly: a position in a claimed symbol is assumed to be ours, so a hand-placed position in
/// an instrument a lane is already trading would be absorbed rather than flagged. Everything in an
/// unclaimed symbol is still treated as foreign, and unattributed exposure still halts entry system-wide.
/// </summary>
public sealed class BrokerExposureAttributor(IEnumerable<IExposureClaimSource> sources)
{
    public BrokerExposureAttribution Attribute(
        IReadOnlyList<BrokerOrderSnapshot> openOrders,
        IReadOnlyList<BrokerPositionSnapshot> positions)
    {
        ArgumentNullException.ThrowIfNull(openOrders);
        ArgumentNullException.ThrowIfNull(positions);

        ExposureClaim[] claims = [.. sources.SelectMany(source => source.ListClaims())];
        HashSet<string> claimedOrderIds = new(
            claims.SelectMany(claim => claim.ClientOrderIds), StringComparer.Ordinal);
        string[] claimedSymbols = [.. claims
            .Select(claim => claim.Symbol)
            .Where(symbol => !string.IsNullOrWhiteSpace(symbol))
            .Distinct(StringComparer.OrdinalIgnoreCase)];

        return new BrokerExposureAttribution(
            [.. openOrders.Where(order => !claimedOrderIds.Contains(order.ClientOrderId))],
            [.. positions
                .Where(position => position.Quantity != 0)
                .Where(position => !claimedSymbols.Any(symbol => BrokerSymbol.Matches(symbol, position.Symbol)))],
            claimedSymbols);
    }
}

/// <summary>
/// Compares symbols across the venue's inconsistent slash convention: the trading API accepts
/// <c>BTC/USD</c> while the positions endpoint reports <c>BTCUSD</c> for the same instrument.
/// </summary>
