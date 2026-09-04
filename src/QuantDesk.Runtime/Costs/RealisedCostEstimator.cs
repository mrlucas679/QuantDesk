using QuantDesk.Domain.Contracts;
using QuantDesk.Domain.Execution;
using QuantDesk.Runtime.Persistence;

namespace QuantDesk.Runtime.Costs;

/// <summary>
/// Derives a <see cref="RealisedCostContract"/> from completed round trips.
///
/// What is measured, and why it is not the fee
/// -------------------------------------------
/// The cost that matters is the gap between the decision and the outcome: implementation shortfall.
/// A decision was made when the reference price was <c>EntryReferencePrice</c>, and if the round
/// trip could have been executed frictionlessly it would have earned the reference-price move. What
/// the account actually did is <c>RealisedAccountPnl</c>. Everything between the two is cost —
/// spread paid on entry, spread paid on exit, slippage against the touch, the in-kind quantity
/// deduction, and the separate USD cash charge.
///
/// That last term is why this cannot be derived from fills. Alpaca deducts a "Coin Pair Transaction
/// Fee (USD)" that appears in neither the fill price nor the filled quantity, so a fee-aware model
/// built from fills reported 36 bps where the account had actually lost 68. Only account equity
/// sees it, so only account equity is admitted here.
///
/// Why a round trip must have held the account alone
/// -------------------------------------------------
/// Account equity is a property of the whole portfolio, not of one position. When two positions are
/// open over the same window, each one's <c>AccountEquityAfter - AccountEquityBefore</c> contains
/// the other's movement in full, so every concurrent round trip claims the entire portfolio's loss
/// as its own. The error is not noise and does not average out: it scales with concurrency, and it
/// scales in the direction that makes trading look more expensive than it is.
///
/// This was live. On 2026-09-02 four spot positions reconciled flat within ten seconds of each
/// other and each recorded roughly -28 USD against a portfolio that had moved about -28 in total.
/// Fed through the shortfall arithmetic below those three measurable trips price at 912, 1,319 and
/// 1,261 bps -- against a round trip that a broker-side reconstruction of the same day measured at
/// 33.7 bps. One more observation and the bucket would have published, and a ~1,160 bps hurdle
/// would have refused every opportunity in the system on a measurement that was an artefact.
///
/// The record now carries what every position was worth at each equity reading, which makes the
/// arithmetic possible for the common case. A sibling that was merely held across the window
/// contributed exactly its own change in market value and nothing to cash, so that contribution is
/// subtracted and what remains belongs to this round trip alone. A sibling that opened or closed
/// inside the window also moved cash, by an amount no arithmetic over marks can separate, and those
/// still refuse -- a cost that is confidently wrong is worse than one that is visibly absent.
///
/// The practical difference is between a lane that can only measure itself while trading one
/// position at a time, and one that can measure itself while holding a book.
/// </summary>
/// <summary>Why a completed round trip could not contribute a cost observation.</summary>
public enum CostRefusal
{
    /// <summary>It could. Not a refusal.</summary>
    None,

    /// <summary>Never completed, or never held anything, so there is no round trip to measure.</summary>
    NotComplete,

    /// <summary>No account equity on both sides, so there is no ground truth to compare against.</summary>
    MissingAccountEquity,

    /// <summary>No entry or exit reference price, so there is no decision to measure shortfall from.</summary>
    MissingDecisionPrice,

    /// <summary>
    /// A sibling opened or closed inside the window, so the equity delta carries cash movement
    /// that cannot be attributed. A sibling merely held across the window is subtracted instead.
    /// </summary>
    SharedTheAccount,
}

/// <summary>
/// How many completed round trips could testify about cost, and why the rest could not.
///
/// Exists because the absence of a measurement was invisible. On 2026-09-02 five of nine completed
/// spot round trips carried no exit reference price and one more had shared the account, so the
/// cost dataset stayed empty -- and the only way to learn that was to read the durable store by
/// hand and check each record. A system that refuses to measure has to say how often it is
/// refusing, or the refusal becomes indistinguishable from there being nothing to measure.
/// </summary>
/// <param name="CompletedRoundTrips">Trips that finished and held something.</param>
/// <param name="Measurable">Trips that produced a cost observation.</param>
/// <param name="MissingAccountEquity">Refused for want of equity readings on both sides.</param>
/// <param name="MissingDecisionPrice">Refused for want of an entry or exit reference price.</param>
/// <param name="SharedTheAccount">Refused because another position was open across the window.</param>
public readonly record struct RealisedCostCoverage(
    int CompletedRoundTrips,
    int Measurable,
    int MissingAccountEquity,
    int MissingDecisionPrice,
    int SharedTheAccount);

public static class RealisedCostEstimator
{
    /// <summary>One-sided 95% normal quantile, for the upper bound on the mean.</summary>
    private const decimal OneSidedNinetyFivePercent = 1.645m;

    /// <summary>
    /// Notional bands, in USD. Chosen to match the sizes this system actually trades rather than to
    /// be evenly spaced: below $25 the fixed components dominate, and above $250 the book depth
    /// starts to matter for the instruments in play.
    /// </summary>
    private static readonly decimal[] BucketEdges = [0m, 25m, 100m, 250m];

    public static RealisedCostContract? Estimate(
        IReadOnlyList<DiagnosticExecutionRecord> records,
        string datasetId,
        string datasetVersion,
        string assetClass,
        string venue,
        IReadOnlyList<SpotExecutionRecord>? spotRecords = null)
    {
        ArgumentNullException.ThrowIfNull(records);

        // Every window in which the account carried an open position, from either lane. Built before
        // any measuring so that a round trip can be checked against the whole account's history and
        // not just its own lane's -- the two lanes share one account, so a diagnostic position open
        // across a spot round trip corrupts that spot round trip exactly as a sibling would.
        IReadOnlyList<ExposureWindow> windows =
        [
            .. records.Select(ExposureWindow.From).OfType<ExposureWindow>(),
            .. (spotRecords ?? []).Select(ExposureWindow.From).OfType<ExposureWindow>(),
        ];

        // Both lanes contribute, because both pay the same venue the same way. Reading only the
        // diagnostic lane was a real limitation rather than a scruple: if the autonomous lane is the
        // one actually trading, a dataset that ignores its round trips stops growing the moment the
        // diagnostic lane stops running, and the cost that gates every decision quietly goes stale.
        List<CostObservation> observations =
        [
            .. records.Select(record => TryMeasure(record, windows)).OfType<CostObservation>(),
            .. (spotRecords ?? []).Select(record => TryMeasureSpot(record, windows))
                .OfType<CostObservation>(),
        ];
        if (observations.Count == 0) return null;

        List<RealisedCostBucket> buckets = [];
        for (int index = 0; index < BucketEdges.Length; index++)
        {
            decimal minimum = BucketEdges[index];
            decimal? maximum = index + 1 < BucketEdges.Length ? BucketEdges[index + 1] : null;
            List<CostObservation> inBucket = [.. observations.Where(item =>
                item.Notional >= minimum && (maximum is null || item.Notional < maximum))];

            if (inBucket.Count < RealisedCostBucket.MinimumRoundTrips) continue;
            buckets.Add(Summarise(minimum, maximum, inBucket));
        }

        if (buckets.Count == 0) return null;

        string executionMode = observations[0].ExecutionMode;
        return new RealisedCostContract(
            datasetId,
            datasetVersion,
            assetClass,
            venue,
            executionMode,
            observations.Min(item => item.CompletedAt),
            observations.Max(item => item.CompletedAt),
            buckets);
    }

    /// <summary>
    /// Counts how many completed round trips could testify, and why the rest could not.
    ///
    /// Reads exactly the same refusals the estimator applies, from the same code, so the two cannot
    /// drift into disagreeing about why a dataset is empty.
    /// </summary>
    public static RealisedCostCoverage Explain(
        IReadOnlyList<DiagnosticExecutionRecord> records,
        IReadOnlyList<SpotExecutionRecord>? spotRecords = null)
    {
        ArgumentNullException.ThrowIfNull(records);

        IReadOnlyList<ExposureWindow> windows =
        [
            .. records.Select(ExposureWindow.From).OfType<ExposureWindow>(),
            .. (spotRecords ?? []).Select(ExposureWindow.From).OfType<ExposureWindow>(),
        ];

        int completed = 0, measurable = 0, equity = 0, price = 0, shared = 0;

        void Tally(CostObservation? observation, CostRefusal refusal)
        {
            // NotComplete is not counted at all: a trip that never finished or never held anything
            // is not a measurement that was lost, it is a measurement that does not exist yet.
            if (refusal is CostRefusal.NotComplete) return;

            completed++;
            if (observation is not null) { measurable++; return; }
            switch (refusal)
            {
                case CostRefusal.MissingAccountEquity: equity++; break;
                case CostRefusal.MissingDecisionPrice: price++; break;
                case CostRefusal.SharedTheAccount: shared++; break;
                default: break;
            }
        }

        foreach (DiagnosticExecutionRecord record in records)
            Tally(TryMeasure(record, windows, out CostRefusal refusal), refusal);

        foreach (SpotExecutionRecord record in spotRecords ?? [])
            Tally(TryMeasureSpot(record, windows, out CostRefusal refusal), refusal);

        return new RealisedCostCoverage(completed, measurable, equity, price, shared);
    }

    private static RealisedCostBucket Summarise(
        decimal minimum,
        decimal? maximum,
        List<CostObservation> inBucket)
    {
        decimal[] costs = [.. inBucket.Select(item => item.CostBps).Order()];
        decimal mean = costs.Sum() / costs.Length;
        decimal median = costs.Length % 2 == 1
            ? costs[costs.Length / 2]
            : (costs[(costs.Length / 2) - 1] + costs[costs.Length / 2]) / 2m;

        // Sample standard deviation, then the standard error of the mean. With one observation the
        // spread is unmeasurable, so the bound collapses to the point estimate and the caller sees a
        // bucket that cannot clear MinimumRoundTrips anyway.
        decimal variance = costs.Length > 1
            ? costs.Sum(cost => (cost - mean) * (cost - mean)) / (costs.Length - 1)
            : 0m;
        decimal standardError = variance > 0m
            ? (decimal)Math.Sqrt((double)variance) / (decimal)Math.Sqrt(costs.Length)
            : 0m;

        return new RealisedCostBucket(
            minimum,
            maximum,
            costs.Length,
            median,
            mean,
            mean + (OneSidedNinetyFivePercent * standardError),
            [.. inBucket.Select(item => item.RecordId)]);
    }

    /// <summary>
    /// One round trip's all-in cost, or null when the trip cannot testify.
    ///
    /// Every rejection here is a case where a number could be computed but would not mean what it
    /// claims: an incomplete trip has no exit to measure, a trip without both equity readings has no
    /// ground truth, a trip without reference prices has no decision point to measure shortfall
    /// against, and a trip that shared the account has no equity delta of its own.
    /// </summary>
    private static CostObservation? TryMeasure(
        DiagnosticExecutionRecord record,
        IReadOnlyList<ExposureWindow> windows) => TryMeasure(record, windows, out _);

    /// <inheritdoc cref="TryMeasure(DiagnosticExecutionRecord, IReadOnlyList{ExposureWindow})"/>
    /// <param name="refusal">Why the trip could not testify, or None when it could.</param>
    private static CostObservation? TryMeasure(
        DiagnosticExecutionRecord record,
        IReadOnlyList<ExposureWindow> windows,
        out CostRefusal refusal)
    {
        refusal = CostRefusal.None;
        if (record.CompletedAt is not { } completedAt) { refusal = CostRefusal.NotComplete; return null; }
        if (record.EntryClientOrderId is not { } recordId) { refusal = CostRefusal.NotComplete; return null; }
        if (record.EntryFilledQuantity <= 0m) { refusal = CostRefusal.NotComplete; return null; }
        if (record.RealisedAccountPnl is not { } realised)
        {
            refusal = CostRefusal.MissingAccountEquity;
            return null;
        }
        if (record.EntryReferencePrice is not { } entryReference || entryReference <= 0m ||
            record.ExitReferencePrice is not { } exitReference)
        {
            refusal = CostRefusal.MissingDecisionPrice;
            return null;
        }
        if (ExposureWindow.From(record) is not { } window) { refusal = CostRefusal.NotComplete; return null; }
        if (window.OverlapsAnyOther(windows)) { refusal = CostRefusal.SharedTheAccount; return null; }

        decimal notional = entryReference * record.EntryFilledQuantity;
        decimal frictionless = (exitReference - entryReference) * record.EntryFilledQuantity;
        decimal cost = frictionless - realised;

        return new CostObservation(
            recordId,
            notional,
            cost / notional * 10_000m,
            record.ExecutionMode,
            completedAt);
    }

    /// <summary>
    /// One autonomous spot round trip's all-in cost, or null when the trip cannot testify.
    ///
    /// The same measure and the same refusals as the diagnostic lane: a decision price to measure
    /// shortfall against, account equity on both sides, and sole ownership of the account over the
    /// window, or nothing. A record that predates this capture has no reference price and is skipped
    /// rather than approximated from its fills, which would read the fee alone and report roughly
    /// half the true cost.
    /// </summary>
    private static CostObservation? TryMeasureSpot(
        SpotExecutionRecord record,
        IReadOnlyList<ExposureWindow> windows) => TryMeasureSpot(record, windows, out _);

    /// <inheritdoc cref="TryMeasureSpot(SpotExecutionRecord, IReadOnlyList{ExposureWindow})"/>
    /// <param name="refusal">Why the trip could not testify, or None when it could.</param>
    private static CostObservation? TryMeasureSpot(
        SpotExecutionRecord record,
        IReadOnlyList<ExposureWindow> windows,
        out CostRefusal refusal)
    {
        refusal = CostRefusal.None;
        if (record.State is not SpotExecutionState.Complete) { refusal = CostRefusal.NotComplete; return null; }
        if (record.CompletedAt is not { } completedAt) { refusal = CostRefusal.NotComplete; return null; }
        if (record.EntryFilledQuantity <= 0m) { refusal = CostRefusal.NotComplete; return null; }
        if (record.RealisedAccountPnl is not { } realised)
        {
            refusal = CostRefusal.MissingAccountEquity;
            return null;
        }
        if (record.EntryReferencePrice is not { } entryReference || entryReference <= 0m ||
            record.ExitReferencePrice is not { } exitReference)
        {
            refusal = CostRefusal.MissingDecisionPrice;
            return null;
        }
        if (ExposureWindow.From(record) is not { } window) { refusal = CostRefusal.NotComplete; return null; }

        // A window that shared the account can still testify, if the siblings only sat there.
        //
        // Account equity is portfolio-wide, so the raw delta contains every other position's
        // movement. But a sibling that was merely held across the window contributed exactly its
        // own change in market value and nothing to cash, and the record now carries both marks --
        // so that contribution can be subtracted and what remains is this position's alone.
        //
        // A sibling that opened or closed inside the window also moved cash, by an amount that
        // cannot be separated without its fills. Those still refuse. This is the difference
        // between a lane that can only measure itself while trading one position at a time, and
        // one that can measure itself while holding a book.
        if (window.OverlapsAnyOther(windows))
        {
            if (SiblingDrift(record) is not { } drift)
            {
                refusal = CostRefusal.SharedTheAccount;
                return null;
            }

            realised -= drift;
        }

        decimal notional = entryReference * record.EntryFilledQuantity;
        decimal frictionless = (exitReference - entryReference) * record.EntryFilledQuantity;

        return new CostObservation(
            record.EntryClientOrderId,
            notional,
            (frictionless - realised) / notional * 10_000m,
            record.ExecutionMode,
            completedAt);
    }

    /// <summary>
    /// What the other positions contributed to this window's equity delta, or null when it cannot
    /// be known.
    ///
    /// Known only when the same set of siblings was present at both readings. A symbol that appears
    /// in one and not the other opened or closed inside the window, which moved cash as well as
    /// market value, and no arithmetic over marks alone can separate that from this position's own
    /// result.
    ///
    /// A sibling marked at zero on either side is treated the same way: it could not be priced, so
    /// its drift is unknown rather than nil, and pretending otherwise would quietly attribute its
    /// movement to this round trip.
    /// </summary>
    private static decimal? SiblingDrift(SpotExecutionRecord record)
    {
        if (record.PositionMarksBefore.Count == 0 || record.PositionMarksAfter.Count == 0) return null;

        Dictionary<string, PositionMark> before = new(StringComparer.OrdinalIgnoreCase);
        foreach (PositionMark mark in record.PositionMarksBefore)
        {
            if (BrokerSymbol.Matches(mark.Symbol, record.Symbol)) continue;
            if (!before.TryAdd(mark.Symbol, mark)) return null;
        }

        Dictionary<string, PositionMark> after = new(StringComparer.OrdinalIgnoreCase);
        foreach (PositionMark mark in record.PositionMarksAfter)
        {
            if (BrokerSymbol.Matches(mark.Symbol, record.Symbol)) continue;
            if (!after.TryAdd(mark.Symbol, mark)) return null;
        }

        if (before.Count != after.Count) return null;

        decimal drift = 0m;
        foreach ((string symbol, PositionMark opening) in before)
        {
            if (!after.TryGetValue(symbol, out PositionMark closing)) return null;
            if (opening.Mid <= 0m || closing.Mid <= 0m) return null;

            // Quantity may differ slightly where a venue takes its fee in kind, so each side is
            // marked on the quantity it actually held.
            drift += closing.MarketValue - opening.MarketValue;
        }

        return drift;
    }

    private readonly record struct CostObservation(
        string RecordId,
        decimal Notional,
        decimal CostBps,
        string ExecutionMode,
        DateTimeOffset CompletedAt);

    /// <summary>
    /// A span during which the account carried an open position, used to tell whether one round
    /// trip's equity delta belongs to it alone.
    ///
    /// The span runs from the reservation, because that is where <c>AccountEquityBefore</c> is read,
    /// to completion, where <c>AccountEquityAfter</c> is read. A position that is still open has no
    /// end, so it is treated as running to the end of time: it is contaminating every round trip
    /// that closes while it is held, and it will keep doing so until it closes.
    /// </summary>
    private readonly record struct ExposureWindow(
        string RecordId,
        DateTimeOffset Start,
        DateTimeOffset End)
    {
        /// <summary>The window a record occupied, or null when it never held anything.</summary>
        public static ExposureWindow? From(SpotExecutionRecord record)
        {
            ArgumentNullException.ThrowIfNull(record);

            // Nothing filled means nothing was held, so the record cannot have moved the account
            // and cannot contaminate a sibling. A rejected order is not exposure.
            if (record.EntryFilledQuantity <= 0m) return null;

            return new ExposureWindow(
                record.EntryClientOrderId,
                record.EntryReservedAt,
                record.CompletedAt ?? DateTimeOffset.MaxValue);
        }

        /// <inheritdoc cref="From(SpotExecutionRecord)"/>
        public static ExposureWindow? From(DiagnosticExecutionRecord record)
        {
            ArgumentNullException.ThrowIfNull(record);

            if (record.EntryFilledQuantity <= 0m) return null;
            if (record.EntryClientOrderId is not { } recordId) return null;

            return new ExposureWindow(
                recordId,
                record.EntryReservedAt ?? record.CreatedAt,
                record.CompletedAt ?? DateTimeOffset.MaxValue);
        }

        /// <summary>
        /// Whether any window other than this one was open at the same time.
        ///
        /// Touching at an endpoint is not an overlap: a position that closed at the instant another
        /// reserved never shared the account, and the equity reading taken at that instant belongs
        /// unambiguously to one of them.
        /// </summary>
        public bool OverlapsAnyOther(IReadOnlyList<ExposureWindow> windows)
        {
            ArgumentNullException.ThrowIfNull(windows);

            foreach (ExposureWindow other in windows)
            {
                if (string.Equals(other.RecordId, RecordId, StringComparison.Ordinal)) continue;
                if (other.Start < End && Start < other.End) return true;
            }

            return false;
        }
    }
}
