using System.Collections.Concurrent;

namespace QuantDesk.Api.PaperTrading;

/// <summary>
/// The most recent closing prices seen for each symbol, shared across every lane.
///
/// Correlation is a property of the account, not of a lane. The crypto and equity lanes run as
/// separate services against one Alpaca account, so a cap on correlated exposure that only saw one
/// lane's symbols would miss exactly the case it exists to catch. This is a singleton for that
/// reason.
///
/// Written from the bars each cycle already fetches, so it costs no extra market-data calls. It is
/// deliberately in memory and lossy: on restart it is empty, and the correlation gate charges an
/// unmeasurable pair the conservative bound rather than assuming independence, so a cold cache
/// makes the lane cautious rather than reckless.
/// </summary>
public sealed class ReturnSeriesCache
{
    /// <summary>
    /// Closes kept per symbol.
    ///
    /// Enough for a correlation over a meaningful window without holding a day of bars per symbol
    /// in a process whose allocation budget is measured.
    /// </summary>
    public const int MaximumCloses = 256;

    private readonly ConcurrentDictionary<string, IReadOnlyList<decimal>> _closes =
        new(StringComparer.OrdinalIgnoreCase);

    public void Record(string symbol, IReadOnlyList<decimal> closes)
    {
        if (string.IsNullOrWhiteSpace(symbol) || closes is null || closes.Count == 0) return;

        _closes[symbol] = closes.Count <= MaximumCloses
            ? [.. closes]
            : [.. closes.Skip(closes.Count - MaximumCloses)];
    }

    public IReadOnlyDictionary<string, IReadOnlyList<decimal>> Snapshot() =>
        new Dictionary<string, IReadOnlyList<decimal>>(_closes, StringComparer.OrdinalIgnoreCase);
}
