using QuantDesk.Domain.Experts;
using QuantDesk.Domain.Forecasts;
using QuantDesk.Domain.Numerics;
using QuantDesk.Runtime.Experts;

namespace QuantDesk.Runtime.Tests.Experts;

public sealed class ExpertCommitteeTests
{
    [Fact]
    public void RequiresFreshCausalConsensus()
    {
        var metadata = new ForecastMetadata(1, 2, ForecastType.DirectionalReturn, TimeSpan.FromMinutes(5), 1, 10, 20, 3, 1, ForecastStatus.Valid);
        var forecast = new DirectionalForecast(metadata, 12, 1, new Probability(0.7), new Probability(0.2), new Probability(0.1), 0.9);
        var result = new ExpertCommittee(0.8, 5).Evaluate(2, [new ExpertVote(1, forecast, 1)], 15, 3);
        Assert.True(result.Actionable);
        Assert.Equal("consensus", result.ReasonCode);
    }

    [Fact]
    public void AbstainsOnStaleEvidence()
    {
        var metadata = new ForecastMetadata(1, 2, ForecastType.DirectionalReturn, TimeSpan.FromMinutes(5), 1, 10, 11, 3, 1, ForecastStatus.Valid);
        var forecast = new DirectionalForecast(metadata, 12, 1, new Probability(0.7), new Probability(0.2), new Probability(0.1), 1);
        var result = new ExpertCommittee(0.8, 5).Evaluate(2, [new ExpertVote(1, forecast, 1)], 12, 3);
        Assert.False(result.Actionable);
        Assert.Equal("insufficient_valid_evidence", result.ReasonCode);
    }
}
