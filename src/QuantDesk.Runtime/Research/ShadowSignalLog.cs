using System.Text.Json;

using QuantDesk.Domain.Trading;

namespace QuantDesk.Runtime.Research;

/// <summary>
/// Signals that the shadow evidence ledger could not be read or committed.
///
/// Evidence is not broker state, but silently discarding it corrupts promotion decisions: an
/// unwritable ledger makes a rule look as though it never fired. Callers may turn this into an
/// explicit degraded/abstained state, but they must not mistake it for an empty healthy ledger.
/// </summary>
public sealed class ShadowSignalPersistenceException : IOException
{
    public ShadowSignalPersistenceException(string operation, string path, Exception innerException)
        : base($"Shadow signal persistence failed during {operation} for '{path}'.", innerException)
    {
        Operation = operation;
        EvidencePath = path;
    }

    public string Operation { get; }

    public string EvidencePath { get; }
}

/// <summary>One firing of a rule, recorded without trading it, and what it went on to earn.</summary>
/// <param name="SignalId">Deterministic identity, so the same firing is never recorded twice.</param>
/// <param name="Symbol">The instrument the rule fired on.</param>
/// <param name="StrategyId">The rule that fired.</param>
/// <param name="FiredAt">When it fired.</param>
/// <param name="EntryReferencePrice">The mid at that moment: the price a trade would have decided at.</param>
/// <param name="ResolveAt">When the holding period ends and the outcome can be read.</param>
/// <param name="VenueRoundTripBps">What a real round trip would have been charged.</param>
/// <param name="ExitReferencePrice">The mid at resolution, or null while still open.</param>
/// <param name="NetBps">The move less the round trip, or null while still open.</param>
public sealed record ShadowSignal(
    string SignalId,
    string Symbol,
    string StrategyId,
    DateTimeOffset FiredAt,
    decimal EntryReferencePrice,
    DateTimeOffset ResolveAt,
    double VenueRoundTripBps)
{
    /// <summary>
    /// Which book this firing belongs to.
    ///
    /// Recorded because the two books share rule identifiers -- <c>reversion.vwap.v1</c> exists in
    /// both, as do the bollinger and rsi reversion rules -- while being different rules held to
    /// costs that differ by more than an order of magnitude. Summarising by identifier alone pools
    /// them, and the pooled figure then decides tradability for both.
    /// </summary>
    public TradedAssetClass AssetClass { get; init; } = InferAssetClass(Symbol);

    /// <summary>
    /// Which way the rule wanted exposure.
    ///
    /// Required as soon as a rule can go short, because a short's outcome is the negative of the
    /// price move. Scoring one as a long inverts its sign, so a rule that shorted a fall correctly
    /// would be recorded as having lost -- and would then be stood down for being right.
    ///
    /// Defaults to Long so the signals recorded before rules had a direction keep the meaning they
    /// were written with.
    /// </summary>
    public SignalDirection Direction { get; init; } = SignalDirection.Long;

    public decimal? ExitReferencePrice { get; init; }

    public double? NetBps { get; init; }

    public bool IsResolved => NetBps is not null;

    /// <summary>
    /// The book a symbol belongs to, for signals recorded before the field existed.
    ///
    /// A pair separated by a slash is a crypto pair; everything else in this system's universe is a
    /// US equity. This interprets history only -- a new signal carries the route's own answer, and
    /// this is never consulted for one of those.
    /// </summary>
    internal static TradedAssetClass InferAssetClass(string symbol) =>
        symbol?.Contains('/', StringComparison.Ordinal) == true
            ? TradedAssetClass.SpotCrypto
            : TradedAssetClass.UsEquity;
}

/// <summary>
/// Records what every rule would have done, so a strategy that is stood down can still earn its
/// way back.
///
/// The dead end this opens
/// -----------------------
/// After the 2026-09-02 re-measurement every rule in both books is known to lose against what the
/// venue actually charges, so the lane opens nothing. That is the correct reading of the evidence,
/// and on its own it is also permanent: a rule that never trades never produces evidence, and
/// without evidence nothing can ever be re-qualified. The desk would sit idle for as long as it
/// ran, with no path back that did not involve someone overriding a gate by hand.
///
/// Section 20.4's ladder exists precisely for this, and SHADOW is the rung that was missing. A rule
/// in shadow is evaluated against live data on every cycle and its outcome recorded, with no order
/// sent and no money at risk. Weeks of that is a live out-of-sample sample -- the only kind that
/// cannot be overfitted, because it is collected after the decision to collect it.
///
/// What is recorded, and what is not
/// ---------------------------------
/// The entry is the mid at the moment the rule fired, which is the price a real decision would have
/// been made at. The exit is the mid when the holding period ends. The difference less the venue's
/// round trip is what the rule would have earned -- honest about costs, and deliberately silent
/// about spread and slippage, which a shadow trade cannot observe because it never touched the
/// book. So a shadow result is an upper bound on what the rule would really have made, and must
/// never be quoted as though it were a fill.
/// </summary>
public sealed class ShadowSignalLog(string path)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>
    /// How many signals are kept.
    ///
    /// Bounded because the constitution forbids an unbounded collection, and because a shadow
    /// sample that reaches back further than the regime it was collected in is not more evidence,
    /// only older evidence. Oldest resolved signals are dropped first.
    /// </summary>
    public const int MaximumSignals = 20_000;

    private readonly Lock _gate = new();

    /// <summary>
    /// The log, held in memory and written through.
    ///
    /// It was read and rewritten in full on every single recorded signal. Measured in production
    /// thirty minutes after this shipped: an 83 KB file rewritten every two to four seconds, and a
    /// lane that records up to ninety-one signals in one cycle was doing ninety-one full
    /// serialisations of a file that only grows. At the twenty-thousand-signal cap that is roughly
    /// eight megabytes, ninety-one times a cycle, on a path the trading loop awaits.
    ///
    /// Holding the map and writing once per batch makes the file cost proportional to cycles rather
    /// than to signals, and reads free. Durability is unchanged where it matters: this is evidence,
    /// not money, and it already fails soft in both directions.
    /// </summary>
    private Dictionary<string, ShadowSignal>? _cache;

    public bool IsAvailable()
    {
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>
    /// Records a firing, or does nothing when this exact firing is already known.
    ///
    /// The identity is the rule, the symbol and the minute, so the same bar evaluated twice by two
    /// cycles is one signal rather than two. Without that the sample would be weighted by how often
    /// the lane happened to run rather than by how often the rule fired.
    /// </summary>
    public bool TryRecord(ShadowSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        return TryRecordMany([signal]) > 0;
    }

    /// <summary>
    /// Records a batch of firings, writing the log at most once however many are new.
    ///
    /// The batch is the unit because the caller's unit is a batch: one evaluation of one instrument
    /// asks every rule and several of them fire together. Recording them one at a time made the
    /// cost of a cycle quadratic in the size of the log.
    /// </summary>
    /// <returns>How many were new.</returns>
    public int TryRecordMany(IReadOnlyList<ShadowSignal> signals)
    {
        ArgumentNullException.ThrowIfNull(signals);
        if (signals.Count == 0) return 0;

        lock (_gate)
        {
            Dictionary<string, ShadowSignal> all = Load();
            int added = 0;

            foreach (ShadowSignal signal in signals)
            {
                if (signal is null || signal.EntryReferencePrice <= 0m) continue;
                if (all.TryAdd(signal.SignalId, signal)) added++;
            }

            if (added > 0) Save(all);
            return added;
        }
    }

    /// <summary>
    /// Closes out every signal whose holding period has ended, given a price for its symbol.
    /// </summary>
    /// <param name="now">The current time.</param>
    /// <param name="midFor">Current mid per symbol, or null when no healthy quote exists.</param>
    /// <returns>How many signals were resolved.</returns>
    public int Resolve(DateTimeOffset now, Func<string, decimal?> midFor)
    {
        ArgumentNullException.ThrowIfNull(midFor);

        lock (_gate)
        {
            Dictionary<string, ShadowSignal> all = Load();
            int resolved = 0;

            foreach (string id in all.Keys.ToArray())
            {
                ShadowSignal signal = all[id];
                if (signal.IsResolved || now < signal.ResolveAt) continue;

                // A signal that cannot be priced stays open rather than being resolved at a guess.
                // It will resolve on a later pass, late but honest, and the lateness is visible in
                // the gap between ResolveAt and the exit it eventually gets.
                if (midFor(signal.Symbol) is not { } exit || exit <= 0m) continue;

                double moveBps =
                    (double)((exit - signal.EntryReferencePrice) / signal.EntryReferencePrice) * 10_000d;

                // Signed by the direction the rule asked for. A short earns what the price gives up,
                // so the move enters with its sign reversed; the round trip is paid either way.
                double directedBps =
                    signal.Direction is SignalDirection.Short ? -moveBps : moveBps;

                all[id] = signal with
                {
                    ExitReferencePrice = exit,
                    NetBps = directedBps - signal.VenueRoundTripBps,
                };
                resolved++;
            }

            if (resolved > 0) Save(all);
            return resolved;
        }
    }

    public IReadOnlyList<ShadowSignal> ListAll()
    {
        lock (_gate) return [.. Load().Values];
    }

    /// <summary>
    /// What each rule has earned in shadow, for rules with enough resolved signals to say.
    ///
    /// The bar is deliberately the same one the research scan uses, so a shadow result and a
    /// backtest result are comparable rather than two different kinds of number.
    /// </summary>
    public IReadOnlyDictionary<string, ShadowSummary> Summarise(int minimumSignals = 12) =>
        Summarise(assetClass: null, minimumSignals);

    /// <summary>
    /// What each rule has earned in shadow within one book, or across all of them when
    /// <paramref name="assetClass"/> is null.
    ///
    /// The filter is the point, and it is not a refinement. The two books share rule identifiers --
    /// <c>reversion.vwap.v1</c>, <c>reversion.bollinger-lower.v1</c> and
    /// <c>reversion.rsi-oversold.v1</c> are each defined in both -- and they are different rules
    /// held to costs that differ by more than an order of magnitude: roughly sixty basis points a
    /// round trip in crypto against a couple in equities.
    ///
    /// Pooled by identifier alone, one summary decided tradability for both books, and the pool is
    /// dominated by crypto: it trades every hour of the day across seven symbols while the equity
    /// book trades six and a half hours across four. So an equity rule would have been promoted at
    /// the opening bell on evidence gathered almost entirely from crypto, and a crypto rule
    /// promoted on a pool diluted by equity signals that never paid a crypto fee. Both directions
    /// are wrong, and this is exactly where it matters, because promotion is the mechanism that
    /// lets a stood-down rule start trading again.
    /// </summary>
    /// <param name="assetClass">The book to summarise, or null for every signal regardless.</param>
    /// <param name="minimumSignals">Resolved signals required before a rule is described at all.</param>
    public IReadOnlyDictionary<string, ShadowSummary> Summarise(
        TradedAssetClass? assetClass, int minimumSignals = 12)
    {
        Dictionary<string, List<double>> byStrategy = new(StringComparer.Ordinal);
        foreach (ShadowSignal signal in ListAll())
        {
            if (signal.NetBps is not { } net) continue;
            if (assetClass is { } book && signal.AssetClass != book) continue;
            if (!byStrategy.TryGetValue(signal.StrategyId, out List<double>? nets))
            {
                nets = [];
                byStrategy[signal.StrategyId] = nets;
            }

            nets.Add(net);
        }

        Dictionary<string, ShadowSummary> summaries = new(StringComparer.Ordinal);
        foreach ((string strategyId, List<double> nets) in byStrategy)
        {
            if (nets.Count < minimumSignals) continue;

            double mean = 0d;
            foreach (double net in nets) mean += net;
            mean /= nets.Count;

            double variance = 0d;
            foreach (double net in nets) variance += (net - mean) * (net - mean);
            double deviation = Math.Sqrt(variance / (nets.Count - 1));

            // The same two-sided 95% bound the registry's figures carry, so the two can be read
            // against each other without converting between conventions.
            double lower = mean - (1.96d * deviation / Math.Sqrt(nets.Count));
            summaries[strategyId] = new ShadowSummary(nets.Count, mean, lower);
        }

        return summaries;
    }

    private Dictionary<string, ShadowSignal> Load()
    {
        if (_cache is not null) return _cache;

        try
        {
            _cache = File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, ShadowSignal>>(
                    File.ReadAllText(path), Json)
                : null;
            _cache ??= new Dictionary<string, ShadowSignal>(StringComparer.Ordinal);
            return _cache;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            throw new ShadowSignalPersistenceException("load", path, exception);
        }
    }

    private void Save(Dictionary<string, ShadowSignal> all)
    {
        if (all.Count > MaximumSignals)
        {
            foreach (string id in all.Values
                .Where(item => item.IsResolved)
                .OrderBy(item => item.FiredAt)
                .Take(all.Count - MaximumSignals)
                .Select(item => item.SignalId)
                .ToArray())
            {
                all.Remove(id);
            }
        }

        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

            // Written through a temporary file so a crash mid-write never leaves a partial log,
            // matching the durable stores on the money path.
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(all, Json));
            File.Move(temporary, path, overwrite: true);
            _cache = all;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ShadowSignalPersistenceException("save", path, exception);
        }
    }
}

/// <summary>What a rule earned in shadow: how many signals, and the mean with its lower bound.</summary>
public readonly record struct ShadowSummary(int Signals, double MeanNetBps, double LowerBoundBps);
