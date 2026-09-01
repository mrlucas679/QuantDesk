using QuantDesk.Domain.Contracts;
using QuantDesk.Domain.Numerics;
using QuantDesk.Domain.Runtime;
using QuantDesk.Domain.Trading;
using QuantDesk.Domain.Strategies;
using QuantDesk.Runtime.Costs;
using QuantDesk.Runtime.State;

namespace QuantDesk.Runtime.Tests.Costs;

public sealed class MeasuredCostFloorTests
{
    [Fact]
    public void MeasurementGovernsWhenTheModelUnderstatesWhatTradingCost()
    {
        // The live defect. The model charges Alpaca's published 50 bps schedule rate; the account
        // lost 68 bps per round trip because the venue also levies a USD cash charge that never
        // appears in a fill. Every candidate whose edge sat between the two looked profitable.
        var floor = new MeasuredCostFloor(new FixedCostModel(50m), Dataset(68m));

        PricedCost priced = floor.Price(Candidate(1000m), Market());

        Assert.Equal(CostBasis.MeasuredExceedsModel, priced.Basis);
        Assert.Equal(6.8m, priced.Estimate.Total.Value);
        Assert.Equal(1.8m, priced.Estimate.MeasuredExcess.Value);
    }

    [Fact]
    public void TheModelKeepsGoverningWhenItIsAlreadyThePessimisticOne()
    {
        // A floor, not a replacement. The spread term is read from the live quote and reflects
        // conditions now; the dataset is an average over trips taken under conditions that have
        // passed. Whichever is currently more pessimistic wins.
        var floor = new MeasuredCostFloor(new FixedCostModel(90m), Dataset(68m));

        PricedCost priced = floor.Price(Candidate(1000m), Market());

        Assert.Equal(CostBasis.MeasuredAndModelAgrees, priced.Basis);
        Assert.Equal(9m, priced.Estimate.Total.Value);
        Assert.Equal(Usd.Zero, priced.Estimate.MeasuredExcess);
    }

    [Fact]
    public void AnUnmeasuredOrderSizeIsMarkedAsAssumptionRatherThanSilentlyPriced()
    {
        // The number is still returned -- refusing to price would break every caller -- but it is
        // labelled, so a caller that requires measurement can abstain instead of discovering later
        // that its "cost" was an assumption nobody had checked.
        var floor = new MeasuredCostFloor(new FixedCostModel(50m), Dataset(68m));

        PricedCost priced = floor.Price(Candidate(50_000m), Market());

        Assert.Equal(CostBasis.Modelled, priced.Basis);
        Assert.False(priced.IsMeasured);
    }

    [Fact]
    public void NoDatasetAtAllIsAnAssumptionNotAFreePass()
    {
        var floor = new MeasuredCostFloor(new FixedCostModel(50m), measured: null);

        Assert.Equal(CostBasis.Modelled, floor.Price(Candidate(1000m), Market()).Basis);
    }

    private static RealisedCostContract Dataset(decimal bps) => new(
        "crypto-alpaca-paper", "v1", "crypto", "alpaca", "PAPER",
        DateTimeOffset.Parse("2026-09-01T00:00:00Z"),
        DateTimeOffset.Parse("2026-09-01T23:59:59Z"),
        [new RealisedCostBucket(0m, 25_000m, 3, bps, bps, bps, ["a", "b", "c"])]);

    private static InstrumentSnapshot Market() => new(
        InstrumentSlot: 0,
        StateVersion: 1,
        Bid: 100d,
        Ask: 100.01d,
        Mid: 100.005d,
        RelativeSpread: 0.0001d,
        LastTrade: 100d,
        Vwap: 100d,
        IntervalVolume: 1_000d,
        OrderBookImbalance: 0d,
        QuoteEventNs: 1,
        TradeEventNs: 1,
        OrderBookEventNs: 1,
        LastReceiveTicks: 1,
        QuoteQuality: DataQuality.Healthy,
        TradeQuality: DataQuality.Healthy,
        OrderBookQuality: DataQuality.Healthy);

    private static TradeCandidate Candidate(decimal notional) => new(
        CandidateId: 1,
        InstrumentSlot: 0,
        StrategyId: "test",
        RiskBasis: RiskBasis.NotionalRisk,
        SourceStateVersion: 1,
        GeneratedMonotonicTicks: 1,
        ValidUntilMonotonicTicks: long.MaxValue,
        GrossExpectedPnl: new Usd(100m),
        EstimatedStressLoss: new Usd(10m),
        Exposure: Exposure(notional),
        ManagementPlan: new PositionManagementPlan(
            TimeSpan.FromMinutes(5), true, false, null, null, "v1"));

    private static EconomicExposure Exposure(decimal notional) => new(
        Notional: new Usd(notional),
        DollarDelta: 0d,
        DollarGamma1Pct: 0d,
        DollarVega1Vol: 0d,
        DollarTheta1Day: 0d,
        EquityBetaUsd: 0d,
        TechBetaUsd: 0d,
        CryptoBetaUsd: 0d,
        GapLoss3Sigma: Usd.Zero,
        GapLoss5Sigma: Usd.Zero,
        ShortConvexityScore: 0d);

    /// <summary>A model that charges a flat bps of notional, so the arithmetic under test is visible.</summary>
    private sealed class FixedCostModel(decimal bps) : ICostModel
    {
        public CostEstimate Estimate(in TradeCandidate candidate, in InstrumentSnapshot market) =>
            new(Usd.Zero, Usd.Zero,
                new Usd(candidate.Exposure.Notional.Value * bps / 10_000m),
                Usd.Zero, Usd.Zero);
    }
}
