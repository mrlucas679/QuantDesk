namespace QuantDesk.Runtime.Indicators;

/// <summary>Which strategy fired, and what is known about it.</summary>
/// <param name="Strategy">The rule that admitted this opportunity.</param>
/// <param name="AlsoFired">Every other strategy that agreed, kept because agreement is evidence too.</param>
public sealed record StrategySelection(SignalStrategy Strategy, IReadOnlyList<string> AlsoFired);

/// <summary>
/// Picks which of the firing strategies gets credited with a trade.
///
/// Why not simply take the best one
/// --------------------------------
/// None of them has qualified, so "best" would mean best by backtest -- and choosing by backtest is
/// how a search of fifteen families becomes one overfitted pick. Worse, the strategies fire at very
/// different rates: dual-horizon momentum fired 1018 times over sixty days where the volatility
/// squeeze fired 118. Letting whichever fires most take every trade would produce a live record
/// that is almost entirely one strategy, and near-silence about the rest.
///
/// So selection is balanced rather than greedy: among the strategies that fired, the one with the
/// fewest live trades so far is chosen. That is the allocation an experiment wants -- comparable
/// sample sizes per arm -- and it is why the user's instruction to mix them is also the
/// statistically correct thing to do.
///
/// Ties break toward the strategy whose *mechanism* has fewest trades, so a set containing four
/// trend rules and one volume rule does not quietly become a trend-only sample.
///
/// A qualified strategy, when one ever exists, is preferred outright. Balanced sampling is for
/// learning about candidates; once something has earned the right to trade on its own merit it
/// should not be held back to keep an experiment tidy.
/// </summary>
public sealed class StrategyRotation
{
    private readonly Dictionary<string, int> _tradesByStrategy = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _tradesByMechanism = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>
    /// Evaluates every strategy at the last bar and selects one, or null when none fired.
    /// </summary>
    public StrategySelection? Select(IReadOnlyList<SignalStrategy> strategies, IndicatorSet indicators)
    {
        ArgumentNullException.ThrowIfNull(strategies);
        ArgumentNullException.ThrowIfNull(indicators);

        int last = indicators.Length - 1;
        List<SignalStrategy> fired = [];
        foreach (SignalStrategy strategy in strategies)
        {
            // A rule that throws on unusual data must not take the lane down with it, and must not
            // be silently counted as having declined either -- it is excluded and the rest proceed.
            try
            {
                if (strategy.Fires(indicators, last)) fired.Add(strategy);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                continue;
            }
        }

        if (fired.Count == 0) return null;

        lock (_gate)
        {
            SignalStrategy chosen = fired
                .OrderByDescending(item => item.Qualification == StrategyQualification.Qualified)
                .ThenBy(item => _tradesByStrategy.GetValueOrDefault(item.Id))
                .ThenBy(item => _tradesByMechanism.GetValueOrDefault(item.Mechanism))
                .ThenBy(item => item.Id, StringComparer.Ordinal)
                .First();

            return new StrategySelection(
                chosen,
                [.. fired.Where(item => item.Id != chosen.Id).Select(item => item.Id).Order(StringComparer.Ordinal)]);
        }
    }

    /// <summary>
    /// Records that a strategy actually opened a position.
    ///
    /// Counted on execution rather than on selection, because a selection that the risk governor or
    /// the cost gate then refused is not a trade, and counting it would push the rotation away from
    /// a strategy that has never actually traded.
    /// </summary>
    public void RecordTrade(SignalStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        lock (_gate)
        {
            _tradesByStrategy[strategy.Id] = _tradesByStrategy.GetValueOrDefault(strategy.Id) + 1;
            _tradesByMechanism[strategy.Mechanism] =
                _tradesByMechanism.GetValueOrDefault(strategy.Mechanism) + 1;
        }
    }

    /// <summary>
    /// Rebuilds the counts from trades that already happened.
    ///
    /// Without this the balance is lost on every restart, and the restart is invisible in the
    /// result. A process that has restarted five times has five independent windows each starting
    /// from zero, so the strategy that fires most often is picked first in every one of them and
    /// the sample tilts toward it -- while the code still looks like it is balancing. That is the
    /// worst kind of experimental defect: it biases the evidence and leaves no trace of having
    /// done so.
    ///
    /// The durable records are the right source because they are what the evidence will eventually
    /// be computed from. Counting anything else would balance against one history and report
    /// against another.
    /// </summary>
    public void RestoreFrom(IEnumerable<string> strategyIds, IReadOnlyList<SignalStrategy> known)
    {
        ArgumentNullException.ThrowIfNull(strategyIds);
        ArgumentNullException.ThrowIfNull(known);

        Dictionary<string, string> mechanisms = known
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Mechanism, StringComparer.Ordinal);

        lock (_gate)
        {
            _tradesByStrategy.Clear();
            _tradesByMechanism.Clear();
            foreach (string id in strategyIds)
            {
                if (string.IsNullOrWhiteSpace(id)) continue;
                _tradesByStrategy[id] = _tradesByStrategy.GetValueOrDefault(id) + 1;

                // A strategy that no longer exists still counted as a trade when it happened, but
                // it has no mechanism to balance against now, so only its own tally is restored.
                if (!mechanisms.TryGetValue(id, out string? mechanism)) continue;
                _tradesByMechanism[mechanism] = _tradesByMechanism.GetValueOrDefault(mechanism) + 1;
            }
        }
    }

    /// <summary>Live trade counts per strategy, for reporting how balanced the sample actually is.</summary>
    public IReadOnlyDictionary<string, int> TradeCounts()
    {
        lock (_gate) return new Dictionary<string, int>(_tradesByStrategy, StringComparer.Ordinal);
    }
}
