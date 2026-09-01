using QuantDesk.Domain.Contracts;
using QuantDesk.Runtime.Costs;
using QuantDesk.Runtime.Persistence;

namespace QuantDesk.Runtime.Tests.Costs;

public sealed class RealisedCostEstimatorTests
{
    [Fact]
    public void CostIsMeasuredAsShortfallAgainstTheDecisionPriceNotAsTheFee()
    {
        // A round trip where the market moved in the strategy's favour and the account still lost.
        // The reference-price move was +$1.00 on 10 units, so a frictionless execution earns $10.
        // The account gained $4, therefore the round trip cost $6 on $1000 of notional: 60 bps.
        //
        // This is the whole point of the measure. A fee model reading the fills would report only
        // the part of that $6 the venue itemised, and would miss the spread crossed on both legs
        // and the separate USD cash charge entirely.
        DiagnosticExecutionRecord record = Completed(
            id: "trip-1", entryReference: 100m, exitReference: 101m, quantity: 10m, realisedPnl: 4m);

        RealisedCostContract contract = Estimate([record, record with { EntryClientOrderId = "trip-2" },
            record with { EntryClientOrderId = "trip-3" }]);

        Assert.Equal(60m, contract.Buckets.Single().MeanBps);
    }

    [Fact]
    public void AProfitableRoundTripStillReportsItsCost()
    {
        // Cost and profit are independent. A trip can pay 60 bps and still make money, and a cost
        // dataset that only recorded losers would understate what trading costs.
        DiagnosticExecutionRecord record = Completed(
            id: "won", entryReference: 100m, exitReference: 102m, quantity: 10m, realisedPnl: 14m);

        RealisedCostContract contract = Estimate([record, record with { EntryClientOrderId = "won-2" },
            record with { EntryClientOrderId = "won-3" }]);

        Assert.Equal(60m, contract.Buckets.Single().MeanBps);
    }

    [Fact]
    public void ARoundTripWithoutAccountEquityCannotTestify()
    {
        // The defect this guards. Fill-derived P&L misses Alpaca's separate USD "Coin Pair
        // Transaction Fee", which appears in no fill price and no filled quantity -- it reported
        // 36 bps where the account had lost 68. A record without the equity reading is therefore
        // not a cheaper measurement, it is a different and wrong one, so it is excluded rather
        // than approximated.
        DiagnosticExecutionRecord blind = Completed("blind", 100m, 101m, 10m, 4m)
            with { RealisedAccountPnl = null };

        Assert.Null(RealisedCostEstimator.Estimate(
            [blind, blind with { EntryClientOrderId = "b2" }, blind with { EntryClientOrderId = "b3" }],
            "d", "v1", "crypto", "alpaca"));
    }

    [Fact]
    public void TooFewRoundTripsProduceNoBucketRatherThanAWideOne()
    {
        DiagnosticExecutionRecord record = Completed("only", 100m, 101m, 10m, 4m);

        Assert.Null(RealisedCostEstimator.Estimate(
            [record, record with { EntryClientOrderId = "second" }],
            "d", "v1", "crypto", "alpaca"));
    }

    [Fact]
    public void TheChargedCostExceedsTheMeanSoMeasurementErrorCountsAgainstTrading()
    {
        // The bound is what gets subtracted from an edge. Charging the mean would accept every
        // candidate whose edge sits inside the measurement error.
        RealisedCostContract contract = Estimate([
            Completed("a", 100m, 101m, 10m, 4m),
            Completed("b", 100m, 101m, 10m, 2m),
            Completed("c", 100m, 101m, 10m, 6m)]);

        RealisedCostBucket bucket = contract.Buckets.Single();
        Assert.True(bucket.UpperConfidenceBps > bucket.MeanBps);
        Assert.Equal(bucket.UpperConfidenceBps, contract.UpperConfidenceCostBpsFor(1000m));
    }

    [Fact]
    public void AnOrderLargerThanAnythingMeasuredHasNoMeasuredCost()
    {
        // Refusing to extrapolate is the point. Inventing a cost for an unmeasured size would
        // fabricate exactly the evidence this contract exists to carry, and the caller must abstain.
        RealisedCostContract contract = Estimate([
            Completed("a", 10m, 10.1m, 1m, 0.04m),
            Completed("b", 10m, 10.1m, 1m, 0.04m),
            Completed("c", 10m, 10.1m, 1m, 0.04m)]);

        Assert.NotNull(contract.UpperConfidenceCostBpsFor(10m));
        Assert.Null(contract.UpperConfidenceCostBpsFor(500m));
    }

    [Fact]
    public void EveryReportedNumberIsTraceableToTheTripsBehindIt()
    {
        RealisedCostContract contract = Estimate([
            Completed("alpha", 100m, 101m, 10m, 4m),
            Completed("beta", 100m, 101m, 10m, 4m),
            Completed("gamma", 100m, 101m, 10m, 4m)]);

        RealisedCostBucket bucket = contract.Buckets.Single();
        Assert.Equal(["alpha", "beta", "gamma"], bucket.SourceRecordIds);
        Assert.Equal(bucket.RoundTripCount, bucket.SourceRecordIds.Count);
        Assert.True(contract.IsValid());
    }

    [Fact]
    public void CostIsBucketedBySizeRatherThanAveragedAcrossIt()
    {
        // Small orders rest inside the touch; larger ones walk the book. One average charges the
        // small order too much and the large one too little.
        List<DiagnosticExecutionRecord> records =
        [
            .. Enumerable.Range(0, 3).Select(i =>
                Completed($"small-{i}", 10m, 10.1m, 1m, 0.09m)),
            .. Enumerable.Range(0, 3).Select(i =>
                Completed($"large-{i}", 100m, 101m, 2m, 1.4m)),
        ];

        RealisedCostContract contract = Estimate(records);

        Assert.Equal(2, contract.Buckets.Count);
        Assert.Equal(6, contract.ObservationCount);
        Assert.True(contract.Buckets[0].MeanBps < contract.Buckets[1].MeanBps);
    }

    private static RealisedCostContract Estimate(IReadOnlyList<DiagnosticExecutionRecord> records)
    {
        RealisedCostContract? contract = RealisedCostEstimator.Estimate(
            records, "crypto-alpaca-paper", "v1", "crypto", "alpaca");
        Assert.NotNull(contract);
        return contract;
    }

    private static DiagnosticExecutionRecord Completed(
        string id,
        decimal entryReference,
        decimal exitReference,
        decimal quantity,
        decimal realisedPnl) =>
        new(
            ExperimentId: "exp",
            Classification: "CryptoSpot",
            Symbol: "BTC/USD",
            State: "Completed",
            RequestedNotional: entryReference * quantity,
            HoldingDuration: TimeSpan.FromMinutes(5),
            CreatedAt: DateTimeOffset.Parse("2026-09-01T12:00:00Z"),
            EntryClientOrderId: id,
            ExitClientOrderId: $"{id}-exit")
        {
            EntryFilledQuantity = quantity,
            EntryReferencePrice = entryReference,
            ExitReferencePrice = exitReference,
            RealisedAccountPnl = realisedPnl,
            CompletedAt = DateTimeOffset.Parse("2026-09-01T12:05:00Z"),
        };
}
