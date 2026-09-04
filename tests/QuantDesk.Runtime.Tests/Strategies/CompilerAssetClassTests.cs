using QuantDesk.Domain.Capabilities;
using QuantDesk.Domain.Forecasts;
using QuantDesk.Domain.Numerics;
using QuantDesk.Domain.Strategies;
using QuantDesk.Domain.Trading;
using QuantDesk.Runtime.Strategies;
using QuantDesk.Runtime.Tests.TestData;
using QuantDesk.Runtime.Time;

namespace QuantDesk.Runtime.Tests.Strategies;

/// <summary>
/// The compiler used to assume every instrument was crypto. Two things followed, neither of which
/// announced itself: it refused a candidate unless the account had crypto permission, whatever was
/// being traded, and it booked the entire position as crypto beta.
/// </summary>
public sealed class CompilerAssetClassTests
{
    [Fact]
    public void AnEquityCandidateNeedsEquityPermissionNotCryptoPermission()
    {
        // An account entitled to equities but not crypto could not trade SPY at all -- refused for
        // lacking a permission the trade never required.
        var withoutCrypto = new AccountCapabilities(
            PaperEnvironment: true, EquityTrading: true, CryptoTrading: false,
            OptionsTrading: false, OptionsTradingLevel: null);

        Assert.Equal(1, Compile(TradedAssetClass.UsEquity, withoutCrypto, out _));
    }

    [Fact]
    public void ACryptoCandidateStillNeedsCryptoPermission()
    {
        var withoutCrypto = new AccountCapabilities(true, true, false, false, null);

        Assert.Equal(0, Compile(TradedAssetClass.SpotCrypto, withoutCrypto, out _));
    }

    [Fact]
    public void AnEquityPositionIsBookedAsEquityBetaNotCryptoBeta()
    {
        // The substantive one. The risk governor reads these fields to enforce per-factor limits, so
        // an equity booked as crypto leaves the equity limit unused and consumes a crypto limit
        // against exposure the portfolio does not hold.
        Assert.Equal(1, Compile(TradedAssetClass.UsEquity, Enabled, out TradeCandidate candidate));

        Assert.Equal(1_000d, candidate.Exposure.EquityBetaUsd);
        Assert.Equal(0d, candidate.Exposure.CryptoBetaUsd);
    }

    [Fact]
    public void ACryptoPositionIsStillBookedAsCryptoBeta()
    {
        Assert.Equal(1, Compile(TradedAssetClass.SpotCrypto, Enabled, out TradeCandidate candidate));

        Assert.Equal(1_000d, candidate.Exposure.CryptoBetaUsd);
        Assert.Equal(0d, candidate.Exposure.EquityBetaUsd);
    }

    [Fact]
    public void TheExitPolicyIsNamedForTheAssetClassItManages()
    {
        Compile(TradedAssetClass.UsEquity, Enabled, out TradeCandidate equity);
        Compile(TradedAssetClass.SpotCrypto, Enabled, out TradeCandidate crypto);

        Assert.Equal("equity-long-managed-v1", equity.ManagementPlan.ExitPolicyVersion);
        Assert.Equal("crypto-long-managed-v1", crypto.ManagementPlan.ExitPolicyVersion);
    }

    private static readonly AccountCapabilities Enabled = new(true, true, true, true, 3);

    private static int Compile(
        TradedAssetClass assetClass, AccountCapabilities capabilities, out TradeCandidate candidate)
    {
        long now = 50;
        var metadata = new ForecastMetadata(
            1, 0, ForecastType.DirectionalReturn, TimeSpan.FromMinutes(5), 10, 40, 100, 1, 1,
            ForecastStatus.Valid);
        Assert.True(Probability.TryCreate(0.6, out Probability up));
        Assert.True(Probability.TryCreate(0.2, out Probability neutral));
        Assert.True(Probability.TryCreate(0.2, out Probability down));
        var bundle = new ForecastBundle(
            0, 1, new DirectionalForecast(metadata, 50, 0.01, up, neutral, down, 0.8));

        var compiler = new CryptoDirectionalStrategyCompiler(
            new Usd(1_000), 0.02, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(60), new LiveRuntimeClock());

        TradeCandidate[] destination = new TradeCandidate[1];
        int written = compiler.Compile(
            bundle, FinancialTestData.HealthyMarket(), FinancialTestData.Portfolio(),
            capabilities, now, assetClass, "directional-v1", destination);
        candidate = destination[0];
        return written;
    }
}
