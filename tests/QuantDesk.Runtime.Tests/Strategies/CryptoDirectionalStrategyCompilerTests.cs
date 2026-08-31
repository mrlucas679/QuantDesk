using QuantDesk.Domain.Capabilities;
using QuantDesk.Domain.Contracts;
using QuantDesk.Domain.Forecasts;
using QuantDesk.Domain.Numerics;
using QuantDesk.Domain.Runtime;
using QuantDesk.Runtime.Strategies;
using QuantDesk.Runtime.Tests.TestData;

namespace QuantDesk.Runtime.Tests.Strategies;

public sealed class CryptoDirectionalStrategyCompilerTests
{
    [Fact]
    public void CompilesLongOnlyCryptoCandidateWithRequiredManagementPlan()
    {
        var forecast = new DirectionalForecast(
            new ForecastMetadata(14, 0, ForecastType.DirectionalReturn, TimeSpan.FromMinutes(5),
                1, 10, 100, 1, 1, ForecastStatus.Valid),
            100, 1, new Probability(0.8), new Probability(0.1), new Probability(0.1), 0.8);
        var bundle = new ForecastBundle(0, 1, forecast);
        var destination = new QuantDesk.Domain.Strategies.TradeCandidate[1];
        var compiler = new CryptoDirectionalStrategyCompiler(
            new Usd(20), 0.05, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15));

        int count = compiler.Compile(bundle, FinancialTestData.HealthyMarket(), FinancialTestData.Portfolio(),
            new AccountCapabilities(true, false, true, false, null), 10, destination);

        Assert.Equal(1, count);
        Assert.Equal("crypto-long-momentum-v1", destination[0].StrategyId);
        Assert.Equal("crypto-long-managed-v1", destination[0].ManagementPlan.ExitPolicyVersion);
        Assert.True(destination[0].ManagementPlan.ExitOnThesisInvalidation);
    }

    [Fact]
    public void NegativeCryptoForecastCompilesToNoCandidate()
    {
        var forecast = new DirectionalForecast(
            new ForecastMetadata(14, 0, ForecastType.DirectionalReturn, TimeSpan.FromMinutes(5),
                1, 10, 100, 1, 1, ForecastStatus.Valid),
            -20, 1, new Probability(0.1), new Probability(0.1), new Probability(0.8), 0.8);
        var destination = new QuantDesk.Domain.Strategies.TradeCandidate[1];
        var compiler = new CryptoDirectionalStrategyCompiler(
            new Usd(20), 0.05, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15));

        int count = compiler.Compile(new ForecastBundle(0, 1, forecast),
            FinancialTestData.HealthyMarket(), FinancialTestData.Portfolio(),
            new AccountCapabilities(true, false, true, false, null), 10, destination);

        Assert.Equal(0, count);
    }

    [Fact]
    public void QualifiedStrategyFamilyIsPreservedForExecutionAttribution()
    {
        var forecast = new DirectionalForecast(
            new ForecastMetadata(14, 0, ForecastType.DirectionalReturn, TimeSpan.FromMinutes(5),
                1, 10, 100, 1, 1, ForecastStatus.Valid),
            100, 1, new Probability(0.8), new Probability(0.1), new Probability(0.1), 0.8);
        var destination = new QuantDesk.Domain.Strategies.TradeCandidate[1];
        var compiler = new CryptoDirectionalStrategyCompiler(
            new Usd(20), 0.05, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15));

        int count = compiler.Compile(new ForecastBundle(0, 1, forecast),
            FinancialTestData.HealthyMarket(), FinancialTestData.Portfolio(),
            new AccountCapabilities(true, false, true, false, null), 10,
            "volatility_breakout", destination);

        Assert.Equal(1, count);
        Assert.Equal("volatility_breakout", destination[0].StrategyId);
    }

    [Fact]
    public void QualifiedArtifactOwnsExactExitSemantics()
    {
        var forecast = new DirectionalForecast(
            new ForecastMetadata(14, 0, ForecastType.DirectionalReturn, TimeSpan.FromHours(12),
                1, 10, 100, 1, 1, ForecastStatus.Valid),
            100, 1, new Probability(0.8), new Probability(0.1), new Probability(0.1), 0.8);
        var definition = new StrategyDefinitionContract(
            "BTC/USD", 60, 720, "compression-breakout-state-v2", "State", "{}",
            new ExitPolicyDefinitionContract("compression-managed-12h-v2", 720, false, true));
        var destination = new QuantDesk.Domain.Strategies.TradeCandidate[1];
        var compiler = new CryptoDirectionalStrategyCompiler(
            new Usd(20), 0.05, TimeSpan.FromMinutes(5), TimeSpan.FromHours(2));

        int count = compiler.Compile(new ForecastBundle(0, 1, forecast),
            FinancialTestData.HealthyMarket(), FinancialTestData.Portfolio(),
            new AccountCapabilities(true, false, true, false, null), 10,
            "compression_breakout", definition, destination);

        Assert.Equal(1, count);
        Assert.Equal(TimeSpan.FromHours(12), destination[0].ManagementPlan.MaximumHoldingPeriod);
        Assert.Equal("compression-managed-12h-v2", destination[0].ManagementPlan.ExitPolicyVersion);
        Assert.False(destination[0].ManagementPlan.ExitOnThesisInvalidation);
        Assert.True(destination[0].ManagementPlan.ExitOnRegimeChange);
    }
}
