using QuantDesk.Api.PaperTrading;
using QuantDesk.Domain.Risk;

namespace QuantDesk.Api.Tests;

/// <summary>
/// These limits were previously ten unnamed literals in the composition root, three of which were
/// set so high they could never bind. The tests pin the properties that made that a defect.
/// </summary>
public sealed class RiskLimitOptionsTests : IDisposable
{
    private static readonly string[] Variables =
    [
        "QUANTDESK_RISK_STRESS_LOSS_PER_TRADE", "QUANTDESK_RISK_MAX_OPEN_RISK",
        "QUANTDESK_RISK_MAX_DAILY_LOSS", "QUANTDESK_RISK_MAX_CAMPAIGN_LOSS",
        "QUANTDESK_RISK_MAX_OPEN_POSITIONS", "QUANTDESK_RISK_MAX_DOLLAR_DELTA",
        "QUANTDESK_RISK_MAX_DOLLAR_GAMMA", "QUANTDESK_RISK_MAX_DOLLAR_VEGA",
        "QUANTDESK_RISK_MAX_RELATIVE_SPREAD", "QUANTDESK_RISK_MAX_SHORT_CONVEXITY"
    ];

    public RiskLimitOptionsTests() => Clear();

    [Fact]
    public void GreeksLimitsScaleWithNotionalSoTheyCanActuallyBind()
    {
        RiskLimits limits = RiskLimitOptions.FromEnvironment(20m);

        // The previous values were 100,000 against a $20 envelope — unreachable by construction.
        Assert.Equal(60d, limits.MaximumAbsDollarDelta);
        Assert.Equal(20d, limits.MaximumAbsDollarGamma1Pct);
        Assert.Equal(20d, limits.MaximumAbsDollarVega1Vol);
        Assert.True(limits.MaximumAbsDollarDelta < 1_000d);
    }

    [Fact]
    public void LossCapsScaleWithTheOrderNotional()
    {
        RiskLimits small = RiskLimitOptions.FromEnvironment(20m);
        RiskLimits large = RiskLimitOptions.FromEnvironment(200m);

        Assert.Equal(20m, small.MaximumStressLossPerTrade.Value);
        Assert.Equal(100m, small.MaximumDailyLoss.Value);
        Assert.Equal(10m * large.MaximumStressLossPerTrade.Value, large.MaximumCampaignLoss.Value);
        // Ten times the notional must give ten times the envelope, not a fixed dollar amount.
        Assert.Equal(10m * small.MaximumDailyLoss.Value, large.MaximumDailyLoss.Value);
    }

    [Fact]
    public void LimitsAreOrderedFromTightestToLoosest()
    {
        RiskLimits limits = RiskLimitOptions.FromEnvironment(20m);

        Assert.True(limits.MaximumStressLossPerTrade.Value <= limits.MaximumOpenRisk.Value);
        Assert.True(limits.MaximumOpenRisk.Value <= limits.MaximumDailyLoss.Value);
        Assert.True(limits.MaximumDailyLoss.Value <= limits.MaximumCampaignLoss.Value);
    }

    [Fact]
    public void EveryLimitCanBeOverriddenFromTheEnvironment()
    {
        Environment.SetEnvironmentVariable("QUANTDESK_RISK_MAX_DOLLAR_DELTA", "12.5");
        Environment.SetEnvironmentVariable("QUANTDESK_RISK_MAX_DAILY_LOSS", "7");
        Environment.SetEnvironmentVariable("QUANTDESK_RISK_MAX_OPEN_POSITIONS", "3");

        RiskLimits limits = RiskLimitOptions.FromEnvironment(20m);

        Assert.Equal(12.5d, limits.MaximumAbsDollarDelta);
        Assert.Equal(7m, limits.MaximumDailyLoss.Value);
        Assert.Equal(3, limits.MaximumOpenPositions);
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("0")]
    [InlineData("-5")]
    public void AnInvalidOverrideFallsBackToTheSafeDefaultRatherThanDisablingTheLimit(string value)
    {
        Environment.SetEnvironmentVariable("QUANTDESK_RISK_MAX_DOLLAR_DELTA", value);

        RiskLimits limits = RiskLimitOptions.FromEnvironment(20m);

        // A zero or negative cap would disable the limit entirely; the default must survive.
        Assert.Equal(60d, limits.MaximumAbsDollarDelta);
    }

    [Fact]
    public void TheProducedEnvelopeAlwaysPassesDomainValidation() =>
        RiskLimitOptions.FromEnvironment(20m).Validate();

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ANonPositiveNotionalIsRejected(decimal notional) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => RiskLimitOptions.FromEnvironment(notional));

    public void Dispose() => Clear();

    private static void Clear()
    {
        foreach (string variable in Variables) Environment.SetEnvironmentVariable(variable, null);
    }
}
