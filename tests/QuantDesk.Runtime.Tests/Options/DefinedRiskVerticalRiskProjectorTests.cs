using QuantDesk.Domain.Numerics;
using QuantDesk.Domain.Options;
using QuantDesk.Domain.Runtime;
using QuantDesk.Domain.Strategies;
using QuantDesk.Domain.Trading;
using QuantDesk.Runtime.Options;

namespace QuantDesk.Runtime.Tests.Options;

public sealed class DefinedRiskVerticalRiskProjectorTests
{
    [Fact]
    public void ProjectsVerifiedLegGreeksAndDefinedLossIntoTheCommonCandidate()
    {
        TradeCandidate directional = Candidate();
        MultiLegOptionCandidate vertical = Vertical();
        var snapshots = new Dictionary<int, OptionRiskSnapshot>
        {
            [10] = new(10, .2, .6, .01, .12, -.02, 1, DataQuality.Healthy),
            [11] = new(11, .2, .4, .005, .08, -.01, 1, DataQuality.Healthy)
        };

        bool projected = DefinedRiskVerticalRiskProjector.TryProject(
            directional, vertical, 600m, new Dictionary<int, int> { [10] = 100, [11] = 100 }, snapshots,
            out TradeCandidate optionCandidate);

        Assert.True(projected);
        Assert.Equal(RiskBasis.DefinedMaximumLoss, optionCandidate.RiskBasis);
        Assert.Equal(20m, optionCandidate.EstimatedStressLoss.Value);
        Assert.Equal(12_000d, optionCandidate.Exposure.DollarDelta);
        Assert.Equal(20m, optionCandidate.Exposure.Notional.Value);
    }

    [Fact]
    public void RejectsMissingOrStaleGreeks()
    {
        bool projected = DefinedRiskVerticalRiskProjector.TryProject(
            Candidate(), Vertical(), 600m, new Dictionary<int, int> { [10] = 100, [11] = 100 },
            new Dictionary<int, OptionRiskSnapshot> { [10] = new(10, 0, 0, 0, 0, 0, 0, DataQuality.Stale) },
            out _);

        Assert.False(projected);
    }

    private static MultiLegOptionCandidate Vertical() => new(1, "spy-bull-call-vertical-v1",
        [new OptionLegCandidate(10, OrderSide.Buy, PositionIntent.BuyToOpen, 1),
         new OptionLegCandidate(11, OrderSide.Sell, PositionIntent.SellToOpen, 1)],
        1m, new Usd(20m), new PositionManagementPlan(TimeSpan.FromHours(1), true, true, new Usd(20m), 7, "exit"));

    private static TradeCandidate Candidate() => new(1, 0, "base", RiskBasis.StressLoss, 1, 1, 100,
        new Usd(30m), new Usd(1m), new EconomicExposure(new Usd(20m), 0, 0, 0, 0, 0, 0, 0, Usd.Zero, Usd.Zero, 0),
        new PositionManagementPlan(TimeSpan.FromHours(1), true, true, null, null, "base"));
}
