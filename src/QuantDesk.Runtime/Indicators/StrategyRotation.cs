using QuantDesk.Domain.Trading;
namespace QuantDesk.Runtime.Indicators;

/// <summary>Which strategy fired, and what is known about it.</summary>
/// <param name="Strategy">The rule that admitted this opportunity.</param>
/// <param name="AlsoFired">Every other strategy that agreed, kept because agreement is evidence too.</param>
/// <param name="Direction">
/// Which way the rule wants exposure. Carried on the selection because the rule decided it and
/// everything downstream needs it -- a selection that dropped the direction would leave the
/// execution path inferring one, which is how every entry became a buy.
/// </param>
public sealed record StrategySelection(
    SignalStrategy Strategy, IReadOnlyList<string> AlsoFired, SignalDirection Direction);

/// <summary>A rule that threw instead of answering, and what it said on the way out.</summary>
/// <param name="StrategyId">The rule that failed.</param>
/// <param name="Reason">The exception's message, kept for the operator rather than for control flow.</param>
public sealed record StrategyFault(string StrategyId, string Reason);

/// <summary>How many times a rule has thrown, and what it said the last time.</summary>
public readonly record struct StrategyFaultTally(int Count, string LastReason);

/// <summary>
/// One evaluation of the strategy book: what was chosen, and what could not answer.
///
/// Why this is not simply a nullable selection
/// -------------------------------------------
/// A rule that throws was being caught and skipped, which made it indistinguishable from a rule
/// that looked at the bar and declined. Those are different facts. "Nothing fired" is a market
/// observation; "nothing fired because every rule threw" is an outage, and a lane that reports the
/// second as the first will sit quiet through a broken indicator set and call it patience.
///
/// So the evaluation carries its failures alongside its result, and a caller that finds no
/// selection can tell which of the two happened.
/// </summary>
/// <param name="Selection">The chosen strategy, or null when none fired.</param>
/// <param name="Faults">Rules that threw during this evaluation.</param>
public sealed record StrategyEvaluation(
    StrategySelection? Selection,
    IReadOnlyList<StrategyFault> Faults)
{
    public static readonly StrategyEvaluation None = new(null, []);

    /// <summary>True when nothing fired and at least one rule could not be asked.</summary>
    public bool Faulted => Selection is null && Faults.Count > 0;
}

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
    private readonly Dictionary<string, StrategyFaultTally> _faults = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>
    /// Evaluates every strategy at the last bar and selects one, reporting any that could not
    /// answer.
    /// </summary>
    public StrategyEvaluation Select(
        IReadOnlyList<SignalStrategy> strategies,
        IndicatorSet indicators,
        IReadOnlyDictionary<string, int>? openByMechanism = null,
        int maximumPerMechanism = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(strategies);
        ArgumentNullException.ThrowIfNull(indicators);

        int last = indicators.Length - 1;
        List<SignalStrategy> fired = [];
        Dictionary<string, SignalDirection> directions = new(StringComparer.Ordinal);
        List<StrategyFault> faults = [];
        foreach (SignalStrategy strategy in strategies)
        {
            // A rule that throws on unusual data must not take the lane down with it. It must not
            // be counted as having declined either: swallowing the throw made a broken rule and a
            // quiet one report the same thing, so the failure is recorded and returned to the
            // caller, which is the distinction between FAILED and ABSTAIN that the rest of the
            // system already keeps.
            try
            {
                SignalDirection direction = strategy.Fires(indicators, last);
                if (direction is not SignalDirection.None)
                {
                    fired.Add(strategy);
                    directions[strategy.Id] = direction;
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                faults.Add(new StrategyFault(strategy.Id, exception.Message));
                RecordFault(strategy.Id, exception.Message);
            }
        }

        if (fired.Count == 0) return new StrategyEvaluation(null, faults);

        // A mechanism that already holds its share stands down, even though it fired.
        //
        // Balancing by trade count cannot equalise across opposing mechanisms, because they never
        // compete: a trend rule fires on rising prices and a reversion rule on oversold ones, so
        // they are almost never candidates on the same bar. The rotation only chooses among
        // strategies that fired together, and trend rules simply fired first -- taking all seven
        // crypto symbols for four hours each. Reversion had no capacity left to use when its own
        // conditions arrived, and would have gone the whole day unsampled while the code looked
        // like it was allocating fairly.
        //
        // Capping concurrent positions per mechanism reserves room for the mechanisms whose turn
        // has not come yet. It costs some trades from whichever mechanism is currently firing,
        // which is the right trade: a day of evidence about one mechanism is worth less than a day
        // of evidence about five.
        if (openByMechanism is not null && maximumPerMechanism < int.MaxValue)
        {
            List<SignalStrategy> withCapacity =
            [
                .. fired.Where(item =>
                    openByMechanism.GetValueOrDefault(item.Mechanism) < maximumPerMechanism),
            ];

            // Nothing is forced through when every firing mechanism is full. Abstaining is the
            // honest outcome: the capacity genuinely is spoken for.
            if (withCapacity.Count == 0) return new StrategyEvaluation(null, faults);
            fired = withCapacity;
        }

        lock (_gate)
        {
            SignalStrategy chosen = fired
                .OrderByDescending(item => item.Qualification == StrategyQualification.Qualified)
                .ThenBy(item => _tradesByStrategy.GetValueOrDefault(item.Id))
                .ThenBy(item => _tradesByMechanism.GetValueOrDefault(item.Mechanism))
                .ThenBy(item => item.Id, StringComparer.Ordinal)
                .First();

            return new StrategyEvaluation(
                new StrategySelection(
                    chosen,
                    [.. fired.Where(item => item.Id != chosen.Id).Select(item => item.Id)
                        .Order(StringComparer.Ordinal)],
                    directions[chosen.Id]),
                faults);
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

    private void RecordFault(string strategyId, string reason)
    {
        lock (_gate)
        {
            int previous = _faults.TryGetValue(strategyId, out StrategyFaultTally tally) ? tally.Count : 0;
            _faults[strategyId] = new StrategyFaultTally(previous + 1, reason);
        }
    }

    /// <summary>
    /// How often each rule has thrown, and what it last said.
    ///
    /// A single throw on an odd bar is not interesting; the same rule throwing on every evaluation
    /// is a rule that has left the book without anyone deciding to remove it. Only a running tally
    /// distinguishes the two, which is why the count is kept here rather than only returned per
    /// evaluation.
    /// </summary>
    public IReadOnlyDictionary<string, StrategyFaultTally> FaultCounts()
    {
        lock (_gate) return new Dictionary<string, StrategyFaultTally>(_faults, StringComparer.Ordinal);
    }
}
