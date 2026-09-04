using QuantDesk.Domain.Trading;
using QuantDesk.Runtime.Indicators;
using QuantDesk.Runtime.Research;

namespace QuantDesk.Runtime.Tests.Indicators;

/// <summary>
/// Live evidence overruling the backtest, which is the only route back for a stood-down rule.
///
/// Every rule in both books is currently known to lose against the venue's measured cost, so
/// nothing trades. Without a promotion path that is permanent: no trades means no evidence, and no
/// evidence means no requalification.
/// </summary>
public sealed class ShadowPromotionTests
{
    private const string Rule = "breakout.bollinger-upper.v1";

    [Fact]
    public void WithNoShadowEvidenceTheBacktestStandsAndNothingTrades()
    {
        Assert.Empty(SignalStrategies.Tradable(TradedAssetClass.SpotCrypto, shadow: null));
    }

    [Fact]
    public void AShadowRecordThatClearsZeroPromotesARuleTheBacktestStoodDown()
    {
        // Better evidence than the scan that stood it down, and for the reason section 20.1 cares
        // about: collected after the decision to collect it, so it cannot have been fitted.
        IReadOnlyList<SignalStrategy> tradable = SignalStrategies.Tradable(
            TradedAssetClass.SpotCrypto,
            new Dictionary<string, ShadowSummary>(StringComparer.Ordinal)
            {
                [Rule] = new(Signals: 40, MeanNetBps: 25d, LowerBoundBps: 8d),
            });

        Assert.Equal([Rule], tradable.Select(item => item.Id));
    }

    [Fact]
    public void PromotionRequiresTheLowerBoundToClearZeroNotTheMean()
    {
        // Shadow figures are an upper bound -- they pay the venue's fee but never crossed the book,
        // so no spread and no slippage. A mean above zero whose interval straddles it is not
        // evidence of an edge.
        Assert.Empty(SignalStrategies.Tradable(
            TradedAssetClass.SpotCrypto,
            new Dictionary<string, ShadowSummary>(StringComparer.Ordinal)
            {
                [Rule] = new(Signals: 40, MeanNetBps: 25d, LowerBoundBps: -10d),
            }));
    }

    [Fact]
    public void TooFewSignalsLeavesTheBacktestInCharge()
    {
        Assert.Empty(SignalStrategies.Tradable(
            TradedAssetClass.SpotCrypto,
            new Dictionary<string, ShadowSummary>(StringComparer.Ordinal)
            {
                [Rule] = new(Signals: 5, MeanNetBps: 100d, LowerBoundBps: 80d),
            }));
    }

    [Fact]
    public void ARuleTheBacktestLikedIsDemotedByAMeasurablyNegativeLiveRecord()
    {
        // The same argument in the other direction. A rule whose live record is negative is not one
        // to keep trading while the backtest catches up.
        string equityRule = SignalStrategies.ForEquity[0].Id;

        IReadOnlyList<SignalStrategy> tradable = SignalStrategies.Tradable(
            TradedAssetClass.UsEquity,
            new Dictionary<string, ShadowSummary>(StringComparer.Ordinal)
            {
                [equityRule] = new(Signals: 60, MeanNetBps: -30d, LowerBoundBps: -50d),
            });

        Assert.DoesNotContain(equityRule, tradable.Select(item => item.Id));
    }

    [Fact]
    public void ShadowCannotRescueARuleWhoseResearchDescribesSomethingElse()
    {
        // A Stale rule's figures were produced by a rule the code no longer computes. Promoting it
        // on shadow would be deciding on one measurement while the recorded one describes another.
        SignalStrategy stale = SignalStrategies.ForCrypto
            .First(item => item.Qualification is StrategyQualification.Stale);

        Assert.DoesNotContain(
            stale.Id,
            SignalStrategies.Tradable(
                TradedAssetClass.SpotCrypto,
                new Dictionary<string, ShadowSummary>(StringComparer.Ordinal)
                {
                    [stale.Id] = new(Signals: 100, MeanNetBps: 200d, LowerBoundBps: 150d),
                })
            .Select(item => item.Id));
    }
}
