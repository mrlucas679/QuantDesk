using QuantDesk.Domain.Contracts;
using QuantDesk.Runtime.Costs;
using QuantDesk.Runtime.Persistence;

namespace QuantDesk.Api.PaperTrading;

/// <summary>
/// Derives the realised-cost dataset from completed round trips, on every read.
///
/// The diagnostic store is the source because it holds the only ground truth: account equity before
/// and after each trip. Alpaca levies a "Coin Pair Transaction Fee (USD)" that appears in neither
/// the fill price nor the filled quantity, so a cost derived from fills is not merely less precise —
/// it is systematically low, which is the error the measurement exists to correct.
///
/// The autonomous lane's own completed round trips contribute alongside it, and have to: if that
/// lane is the one actually trading, a dataset drawn only from the diagnostic lane stops growing
/// the moment the diagnostic lane stops running, and the cost gating every decision goes stale
/// while still looking measured. Its records only began carrying the decision price and the equity
/// readings recently; older ones are skipped rather than approximated from their fills, which would
/// see the fee alone and report roughly half the true cost.
/// </summary>
public sealed class DiagnosticStoreRealisedCostSource(
    DiagnosticExecutionStore store,
    SpotExecutionStore spotStore) : IRealisedCostSource
{
    public RealisedCostContract? Current() => RealisedCostEstimator.Estimate(
        store.ListCompleted(),
        datasetId: "alpaca-paper-realised-cost",
        datasetVersion: "live",
        assetClass: "crypto",
        venue: "alpaca",
        spotStore.ListCompleted());

    public RealisedCostCoverage Coverage() =>
        RealisedCostEstimator.Explain(store.ListCompleted(), spotStore.ListCompleted());
}
