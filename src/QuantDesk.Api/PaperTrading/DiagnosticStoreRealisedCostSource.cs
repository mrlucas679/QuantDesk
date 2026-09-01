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
/// The autonomous lane's own spot records deliberately cannot contribute. They carry fills but no
/// equity readings, so they cannot testify to what a round trip cost. Feeding them in would quietly
/// reintroduce the fill-derived understatement under the name of a measurement.
/// </summary>
public sealed class DiagnosticStoreRealisedCostSource(DiagnosticExecutionStore store)
    : IRealisedCostSource
{
    public RealisedCostContract? Current() => RealisedCostEstimator.Estimate(
        store.ListCompleted(),
        datasetId: "alpaca-paper-realised-cost",
        datasetVersion: "live",
        assetClass: "crypto",
        venue: "alpaca");
}
