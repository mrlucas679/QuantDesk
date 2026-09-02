using QuantDesk.Runtime.Indicators;

namespace QuantDesk.Runtime.Tests.Indicators;

/// <summary>
/// The numbers inside the entry rules, and the guard that stops moving one from silently
/// invalidating the evidence recorded against it.
///
/// Risk limits were moved to configuration long ago; the strategy layer never was, so ADX above 25,
/// RSI crossing 30 and the rest sat as literals inside boolean expressions -- tuning knobs set once,
/// invisible to an operator, impossible to sweep or version.
/// </summary>
public sealed class StrategyThresholdsTests
{
    [Fact]
    public void TheDefaultsAreExactlyWhatTheResearchMeasured()
    {
        // If these drift, every figure in the registry silently stops describing its rule.
        StrategyThresholds measured = StrategyThresholds.Measured;

        Assert.Equal(25d, measured.AdxTrendFloor);
        Assert.Equal(30d, measured.RsiOversoldLevel);
        Assert.Equal(30d, measured.StochasticOversoldCeiling);
        Assert.Equal(2d, measured.VolumeSurgeDeviations);
        Assert.Equal(1.5d, measured.VwapGapAtrs);
        Assert.Equal(50d, measured.RsiTrendFloor);
        Assert.True(measured.IsDefault);
    }

    [Fact]
    public void MovingAThresholdInvalidatesOnlyTheRulesThatReadIt()
    {
        // Per rule rather than one flag: moving the ADX floor says nothing about a Bollinger rule
        // that never reads it, and blanking the whole book would throw away good evidence.
        IReadOnlySet<string> invalidated =
            (StrategyThresholds.Measured with { AdxTrendFloor = 30d }).RulesInvalidatedBy();

        Assert.Equal(["trend.adx-filtered.v1"], invalidated);
    }

    [Fact]
    public void EveryConfigurableThresholdNamesTheRuleItWouldInvalidate()
    {
        // A knob that can be turned without anything noticing is the defect this replaces, so each
        // one has to account for itself.
        Assert.Equal(
            ["trend.adx-filtered.v1"],
            (StrategyThresholds.Measured with { AdxTrendFloor = 1d }).RulesInvalidatedBy());
        Assert.Equal(
            ["reversion.rsi-oversold.v1"],
            (StrategyThresholds.Measured with { RsiOversoldLevel = 1d }).RulesInvalidatedBy());
        Assert.Equal(
            ["reversion.stochastic-oversold.v1"],
            (StrategyThresholds.Measured with { StochasticOversoldCeiling = 1d }).RulesInvalidatedBy());
        Assert.Equal(
            ["volume.surge-breakout.v1"],
            (StrategyThresholds.Measured with { VolumeSurgeDeviations = 1d }).RulesInvalidatedBy());
        Assert.Equal(
            ["reversion.vwap.v1"],
            (StrategyThresholds.Measured with { VwapGapAtrs = 1d }).RulesInvalidatedBy());
        Assert.Equal(
            ["volume.obv-confirmed-trend.v1"],
            (StrategyThresholds.Measured with { RsiTrendFloor = 1d }).RulesInvalidatedBy());
    }

    [Fact]
    public void MovingSeveralInvalidatesSeveral()
    {
        Assert.Equal(
            2,
            (StrategyThresholds.Measured with { AdxTrendFloor = 30d, VwapGapAtrs = 2d })
                .RulesInvalidatedBy().Count);
    }

    [Fact]
    public void TheRunningSystemIsOnTheMeasuredValues()
    {
        // Nothing in the deployed environment overrides them, so every registry figure still
        // describes the rule it names. If this fails, read the environment before the code.
        Assert.True(
            SignalStrategies.Active.IsDefault,
            "A strategy threshold has been overridden; the registry's measured figures no longer describe those rules.");
    }
}
