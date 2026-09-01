using System.Text.Json.Serialization;

namespace QuantDesk.Domain.Contracts;

/// <summary>
/// What trading actually cost, measured from executed round trips rather than a fee schedule.
///
/// Why this type exists
/// --------------------
/// The realised crypto cost was a hand-written constant carrying a prose comment: "68 bps, measured
/// across 59 round trips". That is a claim, not provenance. Nothing tied it to the trips it named,
/// nothing recomputed it when more trips arrived, and the research plane and the execution plane
/// each held their own literal — 60 bps in four Python entry points, 50 bps in the C# scenarios —
/// so the two planes disagreed with each other and with the measurement, silently, in the direction
/// that makes a strategy look tradable.
///
/// A cost figure that licenses trading has to be auditable back to the fills that produced it. This
/// contract carries the observations' identifiers for exactly that reason.
///
/// Cost is a curve, not a scalar
/// -----------------------------
/// Cost rises with size: a notional that rests inside the touch pays the spread once, and one that
/// walks the book pays progressively more. Charging a single average to every order size
/// understates cost for large orders and overstates it for small ones, and the second error rejects
/// real edges just as surely as the first accepts false ones. Buckets keep the size dependence
/// visible instead of averaging it away.
/// </summary>
public sealed record RealisedCostContract(
    string DatasetId,
    string DatasetVersion,
    string AssetClass,
    string Venue,
    string ExecutionMode,
    DateTimeOffset ObservedFrom,
    DateTimeOffset ObservedTo,
    IReadOnlyList<RealisedCostBucket> Buckets)
{
    public bool IsValid() => !string.IsNullOrWhiteSpace(DatasetId)
        && !string.IsNullOrWhiteSpace(DatasetVersion)
        && !string.IsNullOrWhiteSpace(AssetClass)
        && !string.IsNullOrWhiteSpace(Venue)
        && !string.IsNullOrWhiteSpace(ExecutionMode)
        && ObservedTo >= ObservedFrom
        && Buckets.Count > 0
        && Buckets.All(bucket => bucket.IsValid());

    /// <summary>
    /// Total round trips behind this dataset, across every bucket.
    ///
    /// Kept off the wire. It is derived from the buckets, and a derived value transmitted as data
    /// can arrive contradicting what it was derived from -- a consumer would then have two answers
    /// to one question and no way to tell which is stale.
    /// </summary>
    [JsonIgnore]
    public int ObservationCount => Buckets.Sum(bucket => bucket.RoundTripCount);

    /// <summary>
    /// The cost to charge an order of this size, as an upper confidence bound.
    ///
    /// Returns the bound rather than the mean because this figure is subtracted from an edge to
    /// decide whether to trade. A mean is right half the time; charging it means accepting every
    /// candidate whose edge sits inside the measurement error, which is a coin flip dressed as a
    /// decision. The bound makes the estimate's own uncertainty count against trading.
    ///
    /// Returns null when no bucket covers the size. That is deliberate: an order larger than
    /// anything ever measured has no measured cost, and extrapolating one would be inventing the
    /// evidence this type exists to carry. The caller must abstain rather than guess.
    /// </summary>
    public decimal? UpperConfidenceCostBpsFor(decimal notional)
    {
        foreach (RealisedCostBucket bucket in Buckets)
        {
            if (bucket.Covers(notional)) return bucket.UpperConfidenceBps;
        }

        return null;
    }
}

/// <summary>Realised round-trip cost for one notional band.</summary>
/// <param name="MinNotional">Inclusive lower bound of the band.</param>
/// <param name="MaxNotional">Exclusive upper bound, or null for the open-ended top band.</param>
/// <param name="RoundTripCount">Completed round trips measured in this band.</param>
/// <param name="MedianBps">Median all-in cost, robust to a single outlier trip.</param>
/// <param name="MeanBps">Mean all-in cost.</param>
/// <param name="UpperConfidenceBps">One-sided 95% upper bound on the mean.</param>
/// <param name="SourceRecordIds">
/// The round trips behind the numbers, by entry client order ID. Present so a cost that rejected a
/// strategy can be traced to the specific fills that justified it.
/// </param>
public sealed record RealisedCostBucket(
    decimal MinNotional,
    decimal? MaxNotional,
    int RoundTripCount,
    decimal MedianBps,
    decimal MeanBps,
    decimal UpperConfidenceBps,
    IReadOnlyList<string> SourceRecordIds)
{
    /// <summary>
    /// Fewer trips than this and the bound is too wide to mean anything.
    ///
    /// Three is not a statistically motivated number; it is the point below which the standard
    /// error stops constraining anything at all. Treat a bucket at this count as provisional.
    /// </summary>
    public const int MinimumRoundTrips = 3;

    public bool IsValid() => MinNotional >= 0m
        && (MaxNotional is null || MaxNotional > MinNotional)
        && RoundTripCount >= MinimumRoundTrips
        && RoundTripCount == SourceRecordIds.Count
        && SourceRecordIds.All(id => !string.IsNullOrWhiteSpace(id))
        && UpperConfidenceBps >= MeanBps;

    public bool Covers(decimal notional) =>
        notional >= MinNotional && (MaxNotional is null || notional < MaxNotional);
}
