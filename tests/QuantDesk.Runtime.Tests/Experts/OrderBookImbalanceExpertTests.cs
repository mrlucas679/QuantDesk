using QuantDesk.Domain.Forecasts;
using QuantDesk.Domain.Market;
using QuantDesk.Runtime.Experts;

namespace QuantDesk.Runtime.Tests.Experts;

/// <summary>
/// The first expert in this system that looks at something other than price.
///
/// Section 16.2 requires depth imbalance to survive spread, fees, fill uncertainty and adverse
/// selection before it means anything, and none of that has been established -- so what this
/// publishes has to be small, honest about its own calibration, and silent when the book cannot
/// support a claim.
/// </summary>
public sealed class OrderBookImbalanceExpertTests
{
    [Fact]
    public void ABalancedBookPublishesNothing()
    {
        // Depth wanders by a few percent on any liquid pair without meaning anything: BTC/USD read
        // -0.031 and ETH/USD +0.009 on consecutive calls with nothing happening. Publishing those
        // would fill the committee with noise wearing a forecast's clothes.
        Assert.Null(Forecast(imbalance: 0.02d));
    }

    [Fact]
    public void ALeaningBookPublishesAForecastInThatDirection()
    {
        MicrostructureForecast forecast = Forecast(imbalance: 0.6d)!.Value;

        Assert.Equal(0.6d, forecast.OrderBookImbalance, precision: 9);
        Assert.True(forecast.ExpectedReturnBps > 0);
        Assert.Equal(ForecastType.Microstructure, forecast.Metadata.Type);
    }

    [Fact]
    public void ABidHeavyAndAskHeavyBookPublishOppositeSigns()
    {
        Assert.True(Forecast(imbalance: 0.6d)!.Value.ExpectedReturnBps > 0);
        Assert.True(Forecast(imbalance: -0.6d)!.Value.ExpectedReturnBps < 0);
    }

    [Fact]
    public void TheExpectedMoveIsSmallerThanCrossingTheSpreadOnce()
    {
        // Nothing here has been measured. An unmeasured expert that published a large number would
        // dominate a committee it has not earned a place in, so the ceiling is deliberately below
        // what one crossing costs.
        Assert.True(Forecast(imbalance: 1.0d)!.Value.ExpectedReturnBps <= 10d);
    }

    [Fact]
    public void CalibrationIsPublishedAsUnknownRatherThanConfident()
    {
        // A committee that weighs by calibration must not be handed conviction by an expert that
        // has never been scored.
        Assert.Equal(0.5d, Forecast(imbalance: 0.6d)!.Value.CalibrationScore, precision: 9);
    }

    [Fact]
    public void AnUnmeasurableBookPublishesNothing()
    {
        Assert.Null(new OrderBookImbalanceExpert().Forecast(
            new BookImbalance(1.0d, 10m, 0m, 25d, 1, 0),
            0, 30, TimeSpan.FromMinutes(5), 1, 1, 1_000, 1));
    }

    [Fact]
    public void FillProbabilityStaysAProbability()
    {
        Assert.InRange(Forecast(imbalance: 1.0d)!.Value.FillProbability.Value, 0d, 1d);
        Assert.InRange(Forecast(imbalance: -1.0d)!.Value.FillProbability.Value, 0d, 1d);
    }

    private static MicrostructureForecast? Forecast(double imbalance) =>
        new OrderBookImbalanceExpert().Forecast(
            new BookImbalance(imbalance, 10m, 10m, 25d, 3, 3),
            instrumentSlot: 0,
            expertId: 30,
            horizon: TimeSpan.FromMinutes(5),
            eventNs: 1,
            nowMonotonicTicks: 1,
            validUntilMonotonicTicks: 1_000,
            sourceStateVersion: 1);
}
