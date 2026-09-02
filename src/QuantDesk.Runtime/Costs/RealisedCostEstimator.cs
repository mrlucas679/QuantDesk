using QuantDesk.Domain.Contracts;
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
/// </summary>
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

        // Both lanes contribute, because both pay the same venue the same way. Reading only the
        // diagnostic lane was a real limitation rather than a scruple: if the autonomous lane is the
        // one actually trading, a dataset that ignores its round trips stops growing the moment the
        // diagnostic lane stops running, and the cost that gates every decision quietly goes stale.
        List<CostObservation> observations =
        [
            .. records.Select(TryMeasure).OfType<CostObservation>(),
            .. (spotRecords ?? []).Select(TryMeasureSpot).OfType<CostObservation>(),
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
    /// ground truth, and a trip without reference prices has no decision point to measure shortfall
    /// against.
    /// </summary>
    private static CostObservation? TryMeasure(DiagnosticExecutionRecord record)
    {
        if (record.RealisedAccountPnl is not { } realised) return null;
        if (record.EntryReferencePrice is not { } entryReference) return null;
        if (record.ExitReferencePrice is not { } exitReference) return null;
        if (record.CompletedAt is not { } completedAt) return null;
        if (record.EntryClientOrderId is not { } recordId) return null;
        if (record.EntryFilledQuantity <= 0m || entryReference <= 0m) return null;

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
    /// shortfall against, and account equity on both sides, or nothing. A record that predates this
    /// capture has no reference price and is skipped rather than approximated from its fills, which
    /// would read the fee alone and report roughly half the true cost.
    /// </summary>
    private static CostObservation? TryMeasureSpot(SpotExecutionRecord record)
    {
        if (record.State is not SpotExecutionState.Complete) return null;
        if (record.RealisedAccountPnl is not { } realised) return null;
        if (record.EntryReferencePrice is not { } entryReference || entryReference <= 0m) return null;
        if (record.ExitReferencePrice is not { } exitReference) return null;
        if (record.CompletedAt is not { } completedAt) return null;
        if (record.EntryFilledQuantity <= 0m) return null;

        decimal notional = entryReference * record.EntryFilledQuantity;
        decimal frictionless = (exitReference - entryReference) * record.EntryFilledQuantity;

        return new CostObservation(
            record.EntryClientOrderId,
            notional,
            (frictionless - realised) / notional * 10_000m,
            record.ExecutionMode,
            completedAt);
    }

    private readonly record struct CostObservation(
        string RecordId,
        decimal Notional,
        decimal CostBps,
        string ExecutionMode,
        DateTimeOffset CompletedAt);
}
