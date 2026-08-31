using QuantDesk.Runtime.Costs;

namespace QuantDesk.Runtime.Tests.Costs;

public sealed class CryptoFeeScheduleTests
{
    [Fact]
    public void AlpacaTierOneHasAuditableMakerAndTakerProvenance()
    {
        DateTimeOffset retrievedAt = new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);

        CryptoFeeSchedule schedule = CryptoFeeSchedule.AlpacaTier1(retrievedAt);

        Assert.Equal(1, schedule.Tier);
        Assert.Equal(15m, schedule.MakerBps);
        Assert.Equal(25m, schedule.TakerBps);
        Assert.Equal("Alpaca", schedule.Broker);
        Assert.Equal("https://docs.alpaca.markets/us/docs/crypto-fees", schedule.Source);
        Assert.Equal(retrievedAt, schedule.RetrievedAt);
    }
}
