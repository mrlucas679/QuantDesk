using QuantDesk.Runtime.Costs;

namespace QuantDesk.Runtime.Tests.Costs;

public sealed class EquityFeeScheduleTests
{
    [Fact]
    public void Calculates_buy_cat_fee()
    {
        var schedule = EquityFeeSchedule.AlpacaUsNms(DateTimeOffset.UtcNow);
        Assert.Equal(0.000003m, schedule.RegulatoryFee(100m, 1m, false));
    }

    [Fact]
    public void Calculates_sell_fees_and_taf_cap()
    {
        var schedule = EquityFeeSchedule.AlpacaUsNms(DateTimeOffset.UtcNow);
        var fee = schedule.RegulatoryFee(100_000m, 100_000m, true);
        Assert.Equal(0.0000206m * 100_000m + 9.79m + 0.000003m * 100_000m, fee);
    }

    [Fact]
    public void Preserves_account_specific_commission_state()
    {
        var schedule = EquityFeeSchedule.AlpacaUsNms(DateTimeOffset.UtcNow);
        Assert.False(schedule.CommissionFree);
        Assert.Equal("ACCOUNT_SPECIFIC", schedule.CommissionPolicy);
    }
}
