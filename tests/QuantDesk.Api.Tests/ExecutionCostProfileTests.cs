using QuantDesk.Api.PaperTrading;

namespace QuantDesk.Api.Tests;

/// <summary>
/// Costs were modelled purely in basis points, which is scale-invariant and therefore silent about
/// the one thing that makes a small order a guaranteed loss: charges that do not shrink with size.
/// These tests pin the two separate constraints an order must clear — what the venue will accept,
/// and what is worth trading once the broker is paid.
/// </summary>
public sealed class ExecutionCostProfileTests
{
    [Fact]
    public void OptionsCarryAPerContractCostThatEquitiesAndCryptoDoNot()
    {
        // A two-leg vertical pays clearing and regulatory fees per contract per side.
        Assert.Equal(0.20m, ExecutionCostProfile.UsEquityOption.FixedCostPerRoundTripUsd);
        Assert.Equal(0.01m, ExecutionCostProfile.UsEquity.FixedCostPerRoundTripUsd);
        // Crypto fees are a pure percentage, so there is no fixed component to amortise.
        Assert.Equal(0m, ExecutionCostProfile.SpotCryptoTaker.FixedCostPerRoundTripUsd);
    }

    [Fact]
    public void AFixedCostIsARisingShareOfAShrinkingOrder()
    {
        ExecutionCostProfile option = ExecutionCostProfile.UsEquityOption;

        decimal onSmall = option.RoundTripCostUsd(50m, spreadBps: 0m) / 50m * 10_000m;
        decimal onLarge = option.RoundTripCostUsd(327m, spreadBps: 0m) / 327m * 10_000m;

        // The same twenty cents is roughly 40 bps of a $50 spread and 6 bps of a $327 one.
        Assert.True(onSmall > 4 * onLarge - 100m);
        Assert.True(onSmall > onLarge);
    }

    [Fact]
    public void MinimumViableNotionalScalesInverselyWithTheExpectedEdge()
    {
        ExecutionCostProfile option = ExecutionCostProfile.UsEquityOption;

        decimal atStrongEdge = option.MinimumViableNotionalUsd(200m);
        decimal atWeakEdge = option.MinimumViableNotionalUsd(50m);

        // A weaker edge has to be spread over a larger order to stay worth trading.
        Assert.True(atWeakEdge > atStrongEdge);
        Assert.Equal(atStrongEdge * 4m, atWeakEdge);
    }

    [Fact]
    public void APurelyProportionalProfileHasNoMinimumViableSize()
    {
        // Crypto fees scale with the order, so no size is uneconomic on fee grounds alone.
        Assert.Equal(0m, ExecutionCostProfile.SpotCryptoTaker.MinimumViableNotionalUsd(200m));
    }

    [Fact]
    public void AnOrderBelowTheVenueMinimumIsRefusedEvenWithAHugeEdge()
    {
        bool viable = ExecutionCostProfile.SpotCryptoTaker.IsEconomicallyViable(
            notional: 5m, expectedGrossEdgeBps: 5_000m, spreadBps: 1m, out string reason);

        // The venue simply will not take it, however attractive the signal looks.
        Assert.False(viable);
        Assert.Equal("NotionalBelowVenueMinimum", reason);
    }

    [Fact]
    public void AnOrderWhoseCostExceedsItsEdgeIsRefused()
    {
        bool viable = ExecutionCostProfile.UsEquityOption.IsEconomicallyViable(
            notional: 30m, expectedGrossEdgeBps: 10m, spreadBps: 0m, out string reason);

        Assert.False(viable);
        Assert.Equal("CostExceedsExpectedEdge", reason);
    }

    [Fact]
    public void AnOrderThatClearsBothConstraintsIsAccepted()
    {
        bool viable = ExecutionCostProfile.UsEquityOption.IsEconomicallyViable(
            notional: 327m, expectedGrossEdgeBps: 200m, spreadBps: 0m, out string reason);

        Assert.True(viable);
        Assert.Equal("Viable", reason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    public void ANonPositiveNotionalIsNeverViable(decimal notional)
    {
        Assert.False(ExecutionCostProfile.UsEquity.IsEconomicallyViable(
            notional, 200m, 1m, out string reason));
        Assert.Equal("NotionalNotPositive", reason);
    }

    [Fact]
    public void NoExpectedEdgeIsNeverViableAtAnySize()
    {
        Assert.False(ExecutionCostProfile.UsEquity.IsEconomicallyViable(
            100_000m, expectedGrossEdgeBps: 0m, spreadBps: 1m, out string reason));
        Assert.Equal("NoExpectedEdge", reason);
    }

    [Fact]
    public void TheDocumentedOptionsMinimumMatchesWhatTheRunbookTellsOperatorsToConfigure()
    {
        // The runbook tells operators to set a $500 notional rather than $20. This is the
        // arithmetic behind that instruction, at the edge the option gate itself demands.
        decimal minimum = ExecutionCostProfile.UsEquityOption.MinimumViableNotionalUsd(
            ExecutionCostProfile.UsEquityOption.HurdleBps(0m));

        Assert.True(minimum > 20m, "A $20 options order can never be economic.");
        Assert.True(minimum < 500m, "A $500 notional must leave room for a real spread.");
    }
}
