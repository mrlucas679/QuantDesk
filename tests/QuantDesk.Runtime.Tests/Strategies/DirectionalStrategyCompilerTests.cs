using QuantDesk.Domain.Capabilities;
using QuantDesk.Domain.Forecasts;
using QuantDesk.Domain.Numerics;
using QuantDesk.Domain.Strategies;
using QuantDesk.Runtime.State;
using QuantDesk.Runtime.Strategies;
using QuantDesk.Runtime.Tests.TestData;

namespace QuantDesk.Runtime.Tests.Strategies;

public sealed class DirectionalStrategyCompilerTests
{
    [Fact]
    public void Compile_RequiresCausalFreshForecastAndPaperCapability()
    {
        long now = 50;
        var metadata = new ForecastMetadata(1, 0, ForecastType.DirectionalReturn, TimeSpan.FromMinutes(5), 10, 40, 100, 1, 1, ForecastStatus.Valid);
        Assert.True(Probability.TryCreate(0.6, out Probability up));
        Assert.True(Probability.TryCreate(0.2, out Probability neutral));
        Assert.True(Probability.TryCreate(0.2, out Probability down));
        var forecast = new DirectionalForecast(metadata, 50, 0.01, up, neutral, down, 0.8);
        var bundle = new ForecastBundle(0, 1, forecast);
        var compiler = new DirectionalStrategyCompiler(new Usd(1_000), 0.02, TimeSpan.FromMinutes(1));
        TradeCandidate[] destination = new TradeCandidate[1];

        int written = compiler.Compile(bundle, FinancialTestData.HealthyMarket(), FinancialTestData.Portfolio(),
            new AccountCapabilities(true, true, true, true, 3), now, destination);

        Assert.Equal(1, written);
        Assert.Equal(new Usd(5), destination[0].GrossExpectedPnl);
    }

    [Fact]
    public void Compile_DoesNotInventTradeForStaleForecast()
    {
        var metadata = new ForecastMetadata(1, 0, ForecastType.DirectionalReturn, TimeSpan.FromMinutes(5), 10, 40, 20, 1, 1, ForecastStatus.Valid);
        Assert.True(Probability.TryCreate(0.6, out Probability up));
        Assert.True(Probability.TryCreate(0.2, out Probability neutral));
        Assert.True(Probability.TryCreate(0.2, out Probability down));
        var compiler = new DirectionalStrategyCompiler(new Usd(1_000), 0.02, TimeSpan.FromMinutes(1));

        int written = compiler.Compile(
            new ForecastBundle(0, 1, new DirectionalForecast(metadata, 50, 0.01, up, neutral, down, 0.8)),
            FinancialTestData.HealthyMarket(),
            FinancialTestData.Portfolio(),
            new AccountCapabilities(true, true, true, true, 3),
            50,
            new TradeCandidate[1]);

        Assert.Equal(0, written);
    }
}
