using System.Text.Json;
using QuantDesk.Domain.Forecasts;
using QuantDesk.Domain.Scoring;

namespace QuantDesk.Runtime.Scoring;

/// <summary>One forecast, and what the market went on to do about it.</summary>
/// <param name="OutcomeId">Deterministic identity, so the same forecast is recorded once.</param>
/// <param name="EpisodeId">The market realisation this belongs to; several forecasts share one.</param>
/// <param name="ExpertId">Which expert said it.</param>
/// <param name="ForecastType">Which family, and therefore which scoring rule applies.</param>
/// <param name="Symbol">The instrument.</param>
/// <param name="Regime">The regime at the time, so context fit can be read later.</param>
/// <param name="PredictedValue">What was forecast.</param>
/// <param name="ResolveAt">When the horizon ends and the outcome can be read.</param>
/// <param name="ObservedValue">What happened, or null while the horizon is still open.</param>
public sealed record ForecastOutcomeRecord(
    string OutcomeId,
    long EpisodeId,
    int ExpertId,
    ForecastType ForecastType,
    string Symbol,
    string Regime,
    double PredictedValue,
    DateTimeOffset ResolveAt)
{
    public double? ObservedValue { get; init; }

    public bool IsResolved => ObservedValue is not null;
}

/// <summary>
/// Records what each expert forecast and what the market then did, so scoring has something to
/// score.
///
/// Why this is separate from the shadow ledger
/// -------------------------------------------
/// Shadow records what a *strategy* would have earned. This records what an *expert* predicted and
/// whether it was right, which is a different question with a different key and a different metric.
/// Conflating them would reproduce exactly the error section 17.4 forbids: judging a volatility
/// forecast by whether a trade made money.
///
/// The episode is the unit
/// -----------------------
/// Several experts forecasting the same instrument over the same window are describing one market
/// realisation, so they share an episode id. That is what lets the scorer count independent
/// evidence rather than observations -- a distinction that turned a sample of hundreds into a
/// handful of real bets when it was applied to the portfolio.
///
/// Written the way the shadow log had to be rewritten after it melted the disk: held in memory,
/// batched on write, bounded, and failing soft in both directions because this is evidence rather
/// than money.
/// </summary>
public sealed class ForecastOutcomeLog(string path)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>Outcomes retained. Oldest resolved are dropped first.</summary>
    public const int MaximumOutcomes = 20_000;

    private readonly Lock _gate = new();
    private Dictionary<string, ForecastOutcomeRecord>? _cache;

    /// <summary>Records a batch of forecasts, writing at most once however many are new.</summary>
    public int Record(IReadOnlyList<ForecastOutcomeRecord> outcomes)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        if (outcomes.Count == 0) return 0;

        lock (_gate)
        {
            Dictionary<string, ForecastOutcomeRecord> all = Load();
            int added = 0;

            foreach (ForecastOutcomeRecord outcome in outcomes)
            {
                if (outcome is null || !double.IsFinite(outcome.PredictedValue)) continue;
                if (all.TryAdd(outcome.OutcomeId, outcome)) added++;
            }

            if (added > 0) Save(all);
            return added;
        }
    }

    /// <summary>
    /// Closes out every forecast whose horizon has ended, given what actually happened.
    /// </summary>
    /// <param name="now">Current time.</param>
    /// <param name="observedFor">
    /// The realised value for a symbol and family, or null when it cannot be read yet.
    /// </param>
    public int Resolve(DateTimeOffset now, Func<string, ForecastType, double?> observedFor)
    {
        ArgumentNullException.ThrowIfNull(observedFor);

        lock (_gate)
        {
            Dictionary<string, ForecastOutcomeRecord> all = Load();
            int resolved = 0;

            foreach (string id in all.Keys.ToArray())
            {
                ForecastOutcomeRecord outcome = all[id];
                if (outcome.IsResolved || now < outcome.ResolveAt) continue;

                // Unreadable stays open rather than resolving at a guess. It will close on a later
                // pass, late but honest, and the lateness stays visible in the gap between the due
                // time and the value it eventually gets.
                if (observedFor(outcome.Symbol, outcome.ForecastType) is not { } observed) continue;
                if (!double.IsFinite(observed)) continue;

                all[id] = outcome with { ObservedValue = observed };
                resolved++;
            }

            if (resolved > 0) Save(all);
            return resolved;
        }
    }

    /// <summary>Everything resolved, in the shape the scorer consumes.</summary>
    public IReadOnlyList<ExpertForecastOutcome> Resolved()
    {
        lock (_gate)
        {
            return
            [
                .. Load().Values
                    .Where(outcome => outcome.ObservedValue is not null)
                    .Select(outcome => new ExpertForecastOutcome(
                        outcome.EpisodeId,
                        Math.Abs(outcome.OutcomeId.GetHashCode(StringComparison.Ordinal)) + 1L,
                        outcome.ExpertId,
                        outcome.ForecastType,
                        outcome.PredictedValue,
                        outcome.ObservedValue!.Value,
                        PredictedProbability: null,
                        EventOccurred: null,
                        outcome.Regime)),
            ];
        }
    }

    public IReadOnlyList<ExpertForecastScore> Scores() => ExpertForecastScorer.Score(Resolved());

    public int Count
    {
        get { lock (_gate) return Load().Count; }
    }

    private Dictionary<string, ForecastOutcomeRecord> Load()
    {
        if (_cache is not null) return _cache;

        try
        {
            _cache = File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, ForecastOutcomeRecord>>(
                    File.ReadAllText(path), Json)
                : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            _cache = null;
        }

        _cache ??= new Dictionary<string, ForecastOutcomeRecord>(StringComparer.Ordinal);
        return _cache;
    }

    private void Save(Dictionary<string, ForecastOutcomeRecord> all)
    {
        _cache = all;

        if (all.Count > MaximumOutcomes)
        {
            foreach (string id in all.Values
                .Where(item => item.IsResolved)
                .OrderBy(item => item.ResolveAt)
                .Take(all.Count - MaximumOutcomes)
                .Select(item => item.OutcomeId)
                .ToArray())
            {
                all.Remove(id);
            }
        }

        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

            string temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(all, Json));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Evidence, not money. Losing it is bad; stopping the lane is worse.
        }
    }
}
