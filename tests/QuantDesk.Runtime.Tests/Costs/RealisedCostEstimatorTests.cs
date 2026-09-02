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
        RealisedCostContract contract = Estimate([
            Completed("trip-1", 100m, 101m, 10m, 4m),
            Completed("trip-2", 100m, 101m, 10m, 4m),
            Completed("trip-3", 100m, 101m, 10m, 4m)]);

        Assert.Equal(60m, contract.Buckets.Single().MeanBps);
    }

    [Fact]
    public void AProfitableRoundTripStillReportsItsCost()
    {
        // Cost and profit are independent. A trip can pay 60 bps and still make money, and a cost
        // dataset that only recorded losers would understate what trading costs.
        RealisedCostContract contract = Estimate([
            Completed("won-1", 100m, 102m, 10m, 14m),
            Completed("won-2", 100m, 102m, 10m, 14m),
            Completed("won-3", 100m, 102m, 10m, 14m)]);

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
        Assert.Null(RealisedCostEstimator.Estimate(
            [
                Completed("b1", 100m, 101m, 10m, 4m) with { RealisedAccountPnl = null },
                Completed("b2", 100m, 101m, 10m, 4m) with { RealisedAccountPnl = null },
                Completed("b3", 100m, 101m, 10m, 4m) with { RealisedAccountPnl = null },
            ],
            "d", "v1", "crypto", "alpaca"));
    }

    [Fact]
    public void TooFewRoundTripsProduceNoBucketRatherThanAWideOne()
    {
        Assert.Null(RealisedCostEstimator.Estimate(
            [Completed("only", 100m, 101m, 10m, 4m), Completed("second", 100m, 101m, 10m, 4m)],
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
            .. Enumerable.Range(0, 3).Select(i => Completed($"small-{i}", 10m, 10.1m, 1m, 0.09m)),
            .. Enumerable.Range(0, 3).Select(i => Completed($"large-{i}", 100m, 101m, 2m, 1.4m)),
        ];

        RealisedCostContract contract = Estimate(records);

        Assert.Equal(2, contract.Buckets.Count);
        Assert.Equal(6, contract.ObservationCount);
        Assert.True(contract.Buckets[0].MeanBps < contract.Buckets[1].MeanBps);
    }

    // ------------------------------------------------------- sole account ownership

    [Fact]
    public void ARoundTripThatSharedTheAccountCannotTestify()
    {
        // The live defect. Account equity is a portfolio quantity, so when two positions are open
        // together each one's equity delta contains the other's movement in full. On 2026-09-02
        // four spot positions reconciled within ten seconds of each other and each recorded roughly
        // -28 USD against a portfolio that had moved about -28 in total; the shortfall arithmetic
        // then priced them at 912, 1,319 and 1,261 bps against a true round trip of 33.7.
        //
        // Three overlapping trips would otherwise be exactly enough to publish a bucket.
        DateTimeOffset open = DateTimeOffset.Parse("2026-09-02T05:00:00Z");
        DateTimeOffset close = DateTimeOffset.Parse("2026-09-02T09:00:00Z");

        List<DiagnosticExecutionRecord> concurrent =
        [
            .. Enumerable.Range(0, 3).Select(i =>
                Completed($"shared-{i}", 100m, 101m, 10m, 4m)
                    with { CreatedAt = open, EntryReservedAt = open, CompletedAt = close }),
        ];

        Assert.Null(RealisedCostEstimator.Estimate(
            concurrent, "crypto-alpaca-paper", "v1", "crypto", "alpaca"));
    }

    [Fact]
    public void APositionStillOpenContaminatesEveryTripThatClosesBeneathIt()
    {
        // An unclosed position has no end, so it is moving the account through every window that
        // ends while it is held. Three otherwise-clean serialised trips must still be refused.
        DiagnosticExecutionRecord held = Completed("held-through", 100m, 101m, 10m, 4m)
            with
            {
                CreatedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
                EntryReservedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
                CompletedAt = null,
                RealisedAccountPnl = null,
            };

        Assert.Null(RealisedCostEstimator.Estimate(
            [
                held,
                Completed("clean-1", 100m, 101m, 10m, 4m),
                Completed("clean-2", 100m, 101m, 10m, 4m),
                Completed("clean-3", 100m, 101m, 10m, 4m),
            ],
            "crypto-alpaca-paper", "v1", "crypto", "alpaca"));
    }

    [Fact]
    public void ARejectedOrderNeverHeldAnythingSoItContaminatesNothing()
    {
        // Seven equity orders were rejected outright on 2026-09-02 for being submitted outside
        // market hours. They filled nothing, so they moved no equity, and treating them as exposure
        // would discard good measurements to protect against a position that never existed.
        DiagnosticExecutionRecord rejected = Completed("rejected", 100m, 101m, 10m, 4m)
            with
            {
                EntryFilledQuantity = 0m,
                CreatedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
                EntryReservedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
                CompletedAt = null,
            };

        RealisedCostContract contract = Estimate([
            rejected,
            Completed("clean-1", 100m, 101m, 10m, 4m),
            Completed("clean-2", 100m, 101m, 10m, 4m),
            Completed("clean-3", 100m, 101m, 10m, 4m)]);

        Assert.Equal(60m, contract.Buckets.Single().MeanBps);
        Assert.Equal(["clean-1", "clean-2", "clean-3"], contract.Buckets.Single().SourceRecordIds);
    }

    [Fact]
    public void AWindowThatOnlyTouchesAnotherAtItsEdgeStillTestifies()
    {
        // Back-to-back trading is the configuration this measurement is designed for. A position
        // that closes at the instant the next reserves never shared the account, and refusing it
        // would leave the contract unable to fill from a lane that trades one position at a time.
        DateTimeOffset start = DateTimeOffset.Parse("2026-09-02T05:00:00Z");
        List<DiagnosticExecutionRecord> backToBack =
        [
            .. Enumerable.Range(0, 3).Select(i =>
                Completed($"touching-{i}", 100m, 101m, 10m, 4m)
                    with
                    {
                        CreatedAt = start.AddHours(i),
                        EntryReservedAt = start.AddHours(i),
                        CompletedAt = start.AddHours(i + 1),
                    }),
        ];

        Assert.Equal(3, Estimate(backToBack).Buckets.Single().RoundTripCount);
    }

    [Fact]
    public void ASpotTripIsRefusedWhenADiagnosticPositionWasOpenAcrossIt()
    {
        // The two lanes share one account, so contamination crosses between them. This is not
        // hypothetical: the diagnostic lane placed 132 of the day's 151 orders while the spot lane
        // was the one being measured.
        DateTimeOffset open = DateTimeOffset.Parse("2026-09-02T05:00:00Z");

        DiagnosticExecutionRecord diagnostic = Completed("diag-overlap", 100m, 101m, 10m, 4m)
            with
            {
                CreatedAt = open,
                EntryReservedAt = open,
                CompletedAt = open.AddHours(6),
            };

        List<SpotExecutionRecord> spot =
        [
            .. Enumerable.Range(0, 3).Select(i => CompletedSpot(
                $"spot-{i}", open.AddHours(i + 1), open.AddHours(i + 2))),
        ];

        Assert.Null(RealisedCostEstimator.Estimate(
            [diagnostic], "crypto-alpaca-paper", "v1", "crypto", "alpaca", spot));

        // The same three spot trips, with the diagnostic position gone, do testify.
        RealisedCostContract? alone = RealisedCostEstimator.Estimate(
            [], "crypto-alpaca-paper", "v1", "crypto", "alpaca", spot);
        Assert.Equal(3, alone?.Buckets.Single().RoundTripCount);
    }

    // ------------------------------------------------------------------- coverage

    [Fact]
    public void TheReasonADatasetIsEmptyIsReportedRatherThanLeftToBeGuessed()
    {
        // The blind spot this closes. On 2026-09-02 five of nine completed spot round trips carried
        // no exit reference price and the rest had shared the account, so the dataset stayed empty
        // -- and the only way to learn that was to read the durable store by hand and check each
        // record. A system that refuses to measure has to say how often it is refusing, or the
        // refusal is indistinguishable from there being nothing to measure.
        DateTimeOffset open = DateTimeOffset.Parse("2026-09-02T05:00:00Z");

        List<DiagnosticExecutionRecord> records =
        [
            // Two that overlap each other.
            Completed("shared-a", 100m, 101m, 10m, 4m)
                with { CreatedAt = open, EntryReservedAt = open, CompletedAt = open.AddHours(4) },
            Completed("shared-b", 100m, 101m, 10m, 4m)
                with { CreatedAt = open, EntryReservedAt = open, CompletedAt = open.AddHours(4) },
            // One with no decision price.
            Completed("no-price", 100m, 101m, 10m, 4m) with { ExitReferencePrice = null },
            // One with no equity reading.
            Completed("no-equity", 100m, 101m, 10m, 4m) with { RealisedAccountPnl = null },
            // One that can testify.
            Completed("clean", 100m, 101m, 10m, 4m),
        ];

        RealisedCostCoverage coverage = RealisedCostEstimator.Explain(records);

        Assert.Equal(5, coverage.CompletedRoundTrips);
        Assert.Equal(1, coverage.Measurable);
        Assert.Equal(2, coverage.SharedTheAccount);
        Assert.Equal(1, coverage.MissingDecisionPrice);
        Assert.Equal(1, coverage.MissingAccountEquity);
    }

    [Fact]
    public void ATripThatNeverHeldAnythingIsNotCountedAsALostMeasurement()
    {
        // A rejected order is not a measurement that was lost; it is one that does not exist. The
        // seven out-of-hours equity rejections on 2026-09-02 must not inflate the refusal count and
        // make the system look worse at measuring than it is.
        RealisedCostCoverage coverage = RealisedCostEstimator.Explain(
        [
            Completed("rejected", 100m, 101m, 10m, 4m) with { EntryFilledQuantity = 0m },
            Completed("clean", 100m, 101m, 10m, 4m),
        ]);

        Assert.Equal(1, coverage.CompletedRoundTrips);
        Assert.Equal(1, coverage.Measurable);
    }

    [Fact]
    public void CoverageCountsBothLanesBecauseBothShareTheAccount()
    {
        DateTimeOffset open = DateTimeOffset.Parse("2026-09-02T05:00:00Z");

        RealisedCostCoverage coverage = RealisedCostEstimator.Explain(
            [Completed("diag", 100m, 101m, 10m, 4m)
                with { CreatedAt = open, EntryReservedAt = open, CompletedAt = open.AddHours(6) }],
            [CompletedSpot("spot", open.AddHours(1), open.AddHours(2))]);

        Assert.Equal(2, coverage.CompletedRoundTrips);
        Assert.Equal(0, coverage.Measurable);
        Assert.Equal(2, coverage.SharedTheAccount);
    }

    [Fact]
    public void CoverageAgreesWithWhatTheEstimatorActuallyPublished()
    {
        // The two read the same refusals from the same code, and a divergence between "why the
        // dataset is empty" and "what the dataset contains" would be worse than no explanation.
        List<DiagnosticExecutionRecord> records =
        [
            Completed("a", 100m, 101m, 10m, 4m),
            Completed("b", 100m, 101m, 10m, 4m),
            Completed("c", 100m, 101m, 10m, 4m),
            Completed("no-price", 100m, 101m, 10m, 4m) with { ExitReferencePrice = null },
        ];

        RealisedCostContract contract = Estimate(records);
        RealisedCostCoverage coverage = RealisedCostEstimator.Explain(records);

        Assert.Equal(contract.ObservationCount, coverage.Measurable);
        Assert.Equal(4, coverage.CompletedRoundTrips);
        Assert.Equal(1, coverage.MissingDecisionPrice);
    }

    // ---------------------------------------------------------------------- fixtures

    /// <summary>
    /// Hands out a distinct, non-overlapping window to each fixture record.
    ///
    /// Fixtures serialise by default because a set of records sharing one window is not a valid
    /// cost dataset at all -- the equity delta each one reports would be the whole portfolio's.
    /// xUnit constructs this class once per test method, so the sequence is per-test and stable
    /// however the tests are ordered or parallelised. The concurrency tests override the times.
    /// </summary>
    private int _slot;

    private DateTimeOffset NextSlot() =>
        DateTimeOffset.Parse("2026-09-01T00:00:00Z").AddHours(_slot++ * 2);

    private static RealisedCostContract Estimate(IReadOnlyList<DiagnosticExecutionRecord> records)
    {
        RealisedCostContract? contract = RealisedCostEstimator.Estimate(
            records, "crypto-alpaca-paper", "v1", "crypto", "alpaca");
        Assert.NotNull(contract);
        return contract;
    }

    /// <summary>A completed round trip that held the account alone.</summary>
    private DiagnosticExecutionRecord Completed(
        string id,
        decimal entryReference,
        decimal exitReference,
        decimal quantity,
        decimal realisedPnl)
    {
        DateTimeOffset opened = NextSlot();
        return new DiagnosticExecutionRecord(
            ExperimentId: "exp",
            Classification: "CryptoSpot",
            Symbol: "BTC/USD",
            State: "Completed",
            RequestedNotional: entryReference * quantity,
            HoldingDuration: TimeSpan.FromMinutes(5),
            CreatedAt: opened,
            EntryClientOrderId: id,
            ExitClientOrderId: $"{id}-exit")
        {
            EntryReservedAt = opened,
            EntryFilledQuantity = quantity,
            EntryReferencePrice = entryReference,
            ExitReferencePrice = exitReference,
            RealisedAccountPnl = realisedPnl,
            CompletedAt = opened.AddMinutes(5),
        };
    }

    private static SpotExecutionRecord CompletedSpot(
        string id,
        DateTimeOffset reservedAt,
        DateTimeOffset completedAt) =>
        new(
            ExecutionId: id,
            StrategyId: "trend.adx-filtered.v1",
            Symbol: "BTC/USD",
            InstrumentSlot: 1,
            State: SpotExecutionState.Complete,
            EntryClientOrderId: id,
            ExitClientOrderId: $"{id}-exit",
            Quantity: 10m,
            CreatedAt: reservedAt,
            EntryReservedAt: reservedAt)
        {
            EntryFilledQuantity = 10m,
            ExitFilledQuantity = 10m,
            EntryReferencePrice = 100m,
            ExitReferencePrice = 101m,
            AccountEquityBefore = 1000m,
            AccountEquityAfter = 1004m,
            CompletedAt = completedAt,
        };
}
