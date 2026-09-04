using QuantDesk.Domain.Numerics;

namespace QuantDesk.Domain.Portfolio;

/// <summary>What one strategy wants its exposure in one instrument to be.</summary>
/// <param name="StrategyId">The strategy expressing the intent.</param>
/// <param name="Symbol">The instrument, in broker form.</param>
/// <param name="TargetNotional">
/// Signed target exposure. Positive is long, negative is short, and zero is an explicit request to
/// be flat — which is not the same as having no intent at all. A strategy that has decided to close
/// is contributing a decision; a strategy that has expired is contributing nothing.
/// </param>
/// <param name="ArtifactId">The publication that licensed this intent, or null in experimental mode.</param>
/// <param name="ValidUntilTicks">Monotonic deadline after which the intent is stale.</param>
public readonly record struct StrategyIntent(
    string StrategyId,
    string Symbol,
    decimal TargetNotional,
    string? ArtifactId,
    long ValidUntilTicks)
{
    public bool IsLive(long nowTicks) => nowTicks <= ValidUntilTicks;
}

/// <summary>The net exposure a set of strategies wants in one instrument, and who wants it.</summary>
/// <param name="Symbol">The instrument.</param>
/// <param name="NetTargetNotional">Net of every live intent, after the per-symbol cap.</param>
/// <param name="UncappedTargetNotional">What the intents summed to before the cap applied.</param>
/// <param name="WasCapped">True when the cap bound the result, so the reason is visible.</param>
/// <param name="ContributingStrategies">The live strategies behind the number.</param>
public sealed record InstrumentIntent(
    string Symbol,
    decimal NetTargetNotional,
    decimal UncappedTargetNotional,
    bool WasCapped,
    IReadOnlyList<string> ContributingStrategies);

/// <summary>
/// Aggregates what several strategies want into one target per instrument.
///
/// The rule this replaces
/// ----------------------
/// Entry was refused outright whenever any lane already held the instrument: a single "symbol is
/// claimed" test, which meant the system could only ever run one strategy per symbol. That was safe
/// but structural — a second strategy with a genuine view could not express it, and the reason it
/// could not was an execution-level lock rather than anything about risk or capital.
///
/// Netting is what makes several strategies coherent. Two strategies long the same instrument want
/// one larger position, not two competing ones; a long and a short of equal size want no position at
/// all, and trading both would pay the spread twice to hold nothing.
///
/// What is deliberately kept
/// -------------------------
/// This aggregates *intent*. It says what the portfolio should hold, not that holding it is
/// permitted — the risk governor, the capability check, the reconciliation halt, and the
/// unattributed-exposure halt all still apply downstream and none of them are relaxed here. The
/// per-symbol cap is enforced inside the aggregation rather than after it, so no consumer can
/// receive a target above the cap and decide for itself what to do about it.
/// </summary>
public static class PortfolioIntentAggregator
{
    /// <summary>
    /// Nets live intents per instrument, bounded by <paramref name="maximumPerSymbolNotional"/>.
    ///
    /// Expired intents are dropped rather than treated as flat. The distinction matters: a strategy
    /// that has gone silent has not decided to close, so counting its silence as a request to be
    /// flat would let one strategy's outage unwind another's position.
    /// </summary>
    public static IReadOnlyList<InstrumentIntent> Aggregate(
        IReadOnlyList<StrategyIntent> intents,
        long nowTicks,
        Usd maximumPerSymbolNotional)
    {
        ArgumentNullException.ThrowIfNull(intents);
        if (maximumPerSymbolNotional.Value <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumPerSymbolNotional),
                "A per-symbol cap of zero or less would forbid all trading; state a real bound.");

        return [.. intents
            .Where(intent => intent.IsLive(nowTicks) && !string.IsNullOrWhiteSpace(intent.Symbol))
            .GroupBy(intent => intent.Symbol, StringComparer.OrdinalIgnoreCase)
            .Select(group => Net(group.Key, [.. group], maximumPerSymbolNotional))
            .OrderBy(intent => intent.Symbol, StringComparer.OrdinalIgnoreCase)];
    }

    private static InstrumentIntent Net(
        string symbol,
        IReadOnlyList<StrategyIntent> group,
        Usd cap)
    {
        decimal uncapped = group.Sum(intent => intent.TargetNotional);
        decimal capped = Math.Clamp(uncapped, -cap.Value, cap.Value);

        return new InstrumentIntent(
            symbol,
            capped,
            uncapped,
            capped != uncapped,
            [.. group.Select(intent => intent.StrategyId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]);
    }

    /// <summary>
    /// The exposure change needed to move from what is held to what is wanted.
    ///
    /// Returned as a delta rather than a target because that is what an order is. A strategy adding
    /// to a position it already holds should send the increment, not the whole size — sending the
    /// target would double the exposure, which is the specific error the old symbol lock existed to
    /// prevent and the reason netting has to produce deltas to be safe without it.
    /// </summary>
    public static decimal RequiredDelta(in InstrumentIntent intent, decimal currentNotional) =>
        intent.NetTargetNotional - currentNotional;
}
