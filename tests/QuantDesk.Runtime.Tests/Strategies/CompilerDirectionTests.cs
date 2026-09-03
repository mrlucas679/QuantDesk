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
/// The layer that made the whole system long-only below the rules.
///
/// Rules learned to say Short, and every bearish forecast was then dropped here by a single
/// <c>ExpectedReturnBps &lt;= 0</c> clause -- silently, with no reason an operator could read. A
/// negative expected return is a direction, not an invalid forecast.
///
/// Three quantities have to keep the right sign convention once shorts exist, and each is wrong in
/// a different way if treated like the others: expected profit is a magnitude, notional is a
/// magnitude, and beta is signed.
/// </summary>
public sealed class CompilerDirectionTests
{
    [Fact]
    public void ABearishForecastCompilesToAShort()
    {
        Assert.Equal(1, Compile(-50, TradedAssetClass.UsEquity, out TradeCandidate candidate));

        Assert.Equal(SignalDirection.Short, candidate.Direction);
    }

    [Fact]
    public void ABullishForecastStillCompilesToALong()
    {
        Assert.Equal(1, Compile(50, TradedAssetClass.UsEquity, out TradeCandidate candidate));

        Assert.Equal(SignalDirection.Long, candidate.Direction);
    }

    [Fact]
    public void AForecastOfNoMovementIsStillRefused()
    {
        // Zero is not a direction. Relaxing the old clause from <= 0 to == 0 has to keep refusing
        // this case, or the compiler starts producing candidates with no thesis at all.
        Assert.Equal(0, Compile(0, TradedAssetClass.UsEquity, out _));
    }

    [Fact]
    public void AShortIsRefusedOnSpotCryptoWhateverTheForecastSays()
    {
        // Permanent, not pending. Alpaca has no borrow for spot crypto and offers no paper crypto
        // derivative, so a bearish crypto view has to be expressed through options.
        Assert.Equal(0, Compile(-50, TradedAssetClass.SpotCrypto, out _));
        Assert.Equal(1, Compile(50, TradedAssetClass.SpotCrypto, out _));
    }

    [Fact]
    public void ExpectedProfitIsAMagnitudeNotASignedReturn()
    {
        // The one that would have refused every short before anything could explain why. The risk
        // governor's gate is (GrossExpectedPnl - cost) <= 0, so a signed return means a short is
        // rejected as NegativeNetEdge no matter how right it is.
        Compile(-50, TradedAssetClass.UsEquity, out TradeCandidate shortCandidate);
        Compile(50, TradedAssetClass.UsEquity, out TradeCandidate longCandidate);

        // Fifty basis points of a 1,000 notional, expected to be earned either way round.
        Assert.Equal(5m, shortCandidate.GrossExpectedPnl.Value);
        Assert.Equal(longCandidate.GrossExpectedPnl.Value, shortCandidate.GrossExpectedPnl.Value);
    }

    [Fact]
    public void NotionalIsUnsignedBecauseAShortConsumesBuyingPowerToo()
    {
        Compile(-50, TradedAssetClass.UsEquity, out TradeCandidate candidate);

        // The governor refuses when Exposure.Notional > BuyingPower. A negative notional would
        // pass that check unconditionally, which is the opposite of a risk limit.
        Assert.Equal(1_000m, candidate.Exposure.Notional.Value);
    }

    [Fact]
    public void BetaIsSignedBecauseAShortGenuinelyOffsetsALong()
    {
        Compile(-50, TradedAssetClass.UsEquity, out TradeCandidate candidate);

        Assert.Equal(-1_000d, candidate.Exposure.EquityBetaUsd);
        Assert.Equal(0d, candidate.Exposure.CryptoBetaUsd);
    }

    [Fact]
    public void AShortIsManagedByItsOwnExitPolicy()
    {
        // The exit policy version is what the promotion ladder and the attribution records key on.
        // Reusing the long policy's name for a short would make two different management regimes
        // indistinguishable in every record that survives the position.
        Compile(-50, TradedAssetClass.UsEquity, out TradeCandidate shortCandidate);
        Compile(50, TradedAssetClass.UsEquity, out TradeCandidate longCandidate);

        Assert.Equal("equity-short-managed-v1", shortCandidate.ManagementPlan.ExitPolicyVersion);
        Assert.Equal("equity-long-managed-v1", longCandidate.ManagementPlan.ExitPolicyVersion);
    }

    [Fact]
    public void ACandidateNobodyCompiledHasNoDirection()
    {
        // Direction defaults to Long on the constructor so existing call sites keep their meaning,
        // but zeroing the struct bypasses that. None is the honest reading of an uncompiled
        // candidate, and it is refused rather than traded.
        Assert.Equal(SignalDirection.None, default(TradeCandidate).Direction);
    }

    private static int Compile(
        double expectedReturnBps, TradedAssetClass assetClass, out TradeCandidate candidate)
    {
        const long now = 50;
        var metadata = new ForecastMetadata(
            1, 0, ForecastType.DirectionalReturn, TimeSpan.FromMinutes(5), 10, 40, 100, 1, 1,
            ForecastStatus.Valid);
        Assert.True(Probability.TryCreate(0.6, out Probability up));
        Assert.True(Probability.TryCreate(0.2, out Probability neutral));
        Assert.True(Probability.TryCreate(0.2, out Probability down));
        var bundle = new ForecastBundle(
            0, 1, new DirectionalForecast(metadata, expectedReturnBps, 0.01, up, neutral, down, 0.8));

        var compiler = new CryptoDirectionalStrategyCompiler(
            new Usd(1_000), 0.02, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(60),
            new LiveRuntimeClock());

        TradeCandidate[] destination = new TradeCandidate[1];
        int written = compiler.Compile(
            bundle, FinancialTestData.HealthyMarket(), FinancialTestData.Portfolio(),
            new AccountCapabilities(true, true, true, true, 3), now, assetClass,
            "directional-v1", destination);
        candidate = destination[0];
        return written;
    }
}
