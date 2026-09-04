using QuantDesk.Domain.Numerics;
using QuantDesk.Domain.Portfolio;

namespace QuantDesk.Domain.Tests.Portfolio;

public sealed class PortfolioIntentTests
{
    private const long Now = 1_000L;
    private static readonly Usd Cap = new(500m);

    [Fact]
    public void TwoStrategiesWantingTheSameInstrumentProduceOnePosition()
    {
        // What the old rule could not express. Entry was refused whenever any lane already held the
        // symbol, so a second strategy with a genuine view was blocked by an execution-level lock
        // rather than by anything about risk or capital.
        IReadOnlyList<InstrumentIntent> netted = Aggregate(
            Intent("momentum", "BTC/USD", 100m),
            Intent("carry", "BTC/USD", 60m));

        InstrumentIntent intent = Assert.Single(netted);
        Assert.Equal(160m, intent.NetTargetNotional);
        Assert.Equal(["carry", "momentum"], intent.ContributingStrategies);
    }

    [Fact]
    public void OpposingViewsNetToNothingRatherThanTradingTwice()
    {
        // Holding a long and a short of equal size is holding nothing, at the price of two spreads.
        IReadOnlyList<InstrumentIntent> netted = Aggregate(
            Intent("momentum", "BTC/USD", 100m),
            Intent("mean-reversion", "BTC/USD", -100m));

        Assert.Equal(0m, Assert.Single(netted).NetTargetNotional);
    }

    [Fact]
    public void TheNetTargetIsCappedAndSaysThatItWas()
    {
        // The cap is applied inside the aggregation so no consumer can receive a target above it
        // and decide for itself what to do.
        IReadOnlyList<InstrumentIntent> netted = Aggregate(
            Intent("a", "BTC/USD", 400m),
            Intent("b", "BTC/USD", 400m));

        InstrumentIntent intent = Assert.Single(netted);
        Assert.Equal(500m, intent.NetTargetNotional);
        Assert.Equal(800m, intent.UncappedTargetNotional);
        Assert.True(intent.WasCapped);
    }

    [Fact]
    public void TheCapBindsShortExposureToo()
    {
        IReadOnlyList<InstrumentIntent> netted = Aggregate(Intent("a", "BTC/USD", -900m));

        Assert.Equal(-500m, Assert.Single(netted).NetTargetNotional);
    }

    [Fact]
    public void AnExpiredIntentContributesNothingRatherThanRequestingFlat()
    {
        // The distinction that keeps one strategy's outage from unwinding another's position. A
        // strategy that has gone silent has not decided to close.
        IReadOnlyList<InstrumentIntent> netted = Aggregate(
            Intent("live", "BTC/USD", 100m),
            Intent("stale", "BTC/USD", -100m, validUntil: Now - 1));

        InstrumentIntent intent = Assert.Single(netted);
        Assert.Equal(100m, intent.NetTargetNotional);
        Assert.Equal(["live"], intent.ContributingStrategies);
    }

    [Fact]
    public void AnExplicitFlatIsADecisionAndStillCounts()
    {
        // Zero is a strategy saying "be flat", which is different from saying nothing. It appears
        // as a contributor so the resulting target can be explained.
        IReadOnlyList<InstrumentIntent> netted = Aggregate(
            Intent("closing", "BTC/USD", 0m),
            Intent("holding", "BTC/USD", 80m));

        Assert.Equal(["closing", "holding"], Assert.Single(netted).ContributingStrategies);
    }

    [Fact]
    public void DifferentInstrumentsAreNettedSeparately()
    {
        IReadOnlyList<InstrumentIntent> netted = Aggregate(
            Intent("a", "BTC/USD", 100m),
            Intent("b", "ETH/USD", 70m));

        Assert.Equal(2, netted.Count);
        Assert.Equal(["BTC/USD", "ETH/USD"], netted.Select(intent => intent.Symbol));
    }

    [Fact]
    public void AnOrderIsTheDifferenceBetweenHeldAndWantedNotTheWholeTarget()
    {
        // The error the old symbol lock existed to prevent. A strategy adding to a position it
        // already holds must send the increment; sending the target would double the exposure, so
        // netting has to produce deltas to be safe without the lock.
        InstrumentIntent intent = Assert.Single(Aggregate(
            Intent("a", "BTC/USD", 100m), Intent("b", "BTC/USD", 60m)));

        Assert.Equal(60m, PortfolioIntentAggregator.RequiredDelta(intent, currentNotional: 100m));
        Assert.Equal(160m, PortfolioIntentAggregator.RequiredDelta(intent, currentNotional: 0m));
        Assert.Equal(-40m, PortfolioIntentAggregator.RequiredDelta(intent, currentNotional: 200m));
    }

    [Fact]
    public void AnAlreadyCorrectPositionRequiresNoOrder()
    {
        InstrumentIntent intent = Assert.Single(Aggregate(Intent("a", "BTC/USD", 100m)));

        Assert.Equal(0m, PortfolioIntentAggregator.RequiredDelta(intent, currentNotional: 100m));
    }

    [Fact]
    public void ACapOfZeroIsRefusedRatherThanSilentlyForbiddingAllTrading()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PortfolioIntentAggregator.Aggregate(
            [Intent("a", "BTC/USD", 100m)], Now, Usd.Zero));
    }

    [Fact]
    public void SymbolsAreMatchedWithoutRegardToCase()
    {
        IReadOnlyList<InstrumentIntent> netted = Aggregate(
            Intent("a", "BTC/USD", 100m), Intent("b", "btc/usd", 50m));

        Assert.Equal(150m, Assert.Single(netted).NetTargetNotional);
    }

    private static IReadOnlyList<InstrumentIntent> Aggregate(params StrategyIntent[] intents) =>
        PortfolioIntentAggregator.Aggregate(intents, Now, Cap);

    private static StrategyIntent Intent(
        string strategyId, string symbol, decimal target, long validUntil = Now + 1) =>
        new(strategyId, symbol, target, "artifact-1", validUntil);
}
