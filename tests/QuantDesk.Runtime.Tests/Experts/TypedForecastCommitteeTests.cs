using QuantDesk.Domain.Experts;
using QuantDesk.Domain.Forecasts;
using QuantDesk.Domain.Numerics;
using QuantDesk.Runtime.Experts;

namespace QuantDesk.Runtime.Tests.Experts;

/// <summary>
/// Aggregating within a forecast family, and the probability invariant that guards it.
///
/// Section 26.2 lists "probabilities finite, bounded and normalized" as a financial invariant and
/// Appendix A repeats it as a golden oracle. The committee had no test at all, and its normaliser
/// took <c>params double[]</c> and wrote into the array it had just allocated -- so the caller's
/// values were never touched and every published probability set was whatever the weighted average
/// happened to produce. The only reason that was visible is that the call sites reached for
/// <c>ref</c>, which does not compile with <c>params</c>. Written the other way round it would have
/// compiled and shipped.
/// </summary>
public sealed class TypedForecastCommitteeTests
{
    [Fact]
    public void DirectionalProbabilitiesArePublishedNormalized()
    {
        // Three experts whose raw probabilities sum to well over one. Before, they were published
        // as-is; a consumer reading them as a distribution would have been reading nonsense.
        ForecastFamilyDecision<DirectionalForecast> decision = Committee().EvaluateDirectional(
            instrumentSlot: 0,
            votes:
            [
                Vote(1, up: 0.9, neutral: 0.7, down: 0.5),
                Vote(2, up: 0.8, neutral: 0.6, down: 0.4),
                Vote(3, up: 0.7, neutral: 0.5, down: 0.3),
            ],
            nowMonotonicTicks: 10,
            sourceStateVersion: 1,
            expectedExperts: 3);

        Assert.True(decision.HasForecast, decision.ReasonCode);

        DirectionalForecast forecast = decision.Forecast!.Value;
        double sum = forecast.ProbabilityUp.Value
            + forecast.ProbabilityNeutral.Value
            + forecast.ProbabilityDown.Value;

        Assert.Equal(1d, sum, precision: 9);
    }

    [Fact]
    public void EachProbabilityStaysInsideItsOwnBounds()
    {
        ForecastFamilyDecision<DirectionalForecast> decision = Committee().EvaluateDirectional(
            0,
            [
                Vote(1, up: 1.0, neutral: 0.0, down: 0.0),
                Vote(2, up: 1.0, neutral: 0.0, down: 0.0),
                Vote(3, up: 1.0, neutral: 0.0, down: 0.0),
            ],
            10, 1, expectedExperts: 3);

        Assert.True(decision.HasForecast, decision.ReasonCode);

        DirectionalForecast forecast = decision.Forecast!.Value;
        Assert.InRange(forecast.ProbabilityUp.Value, 0d, 1d);
        Assert.InRange(forecast.ProbabilityNeutral.Value, 0d, 1d);
        Assert.InRange(forecast.ProbabilityDown.Value, 0d, 1d);
        Assert.Equal(1d, forecast.ProbabilityUp.Value, precision: 9);
    }

    [Fact]
    public void TheDirectionOfTheAggregateSurvivesNormalisation()
    {
        // Normalising must rescale, not reorder. A book of up-leaning experts has to stay
        // up-leaning, or the correction would be worse than the defect it replaced.
        DirectionalForecast forecast = Committee().EvaluateDirectional(
            0,
            [
                Vote(1, up: 0.8, neutral: 0.15, down: 0.05),
                Vote(2, up: 0.7, neutral: 0.2, down: 0.1),
                Vote(3, up: 0.9, neutral: 0.05, down: 0.05),
            ],
            10, 1, expectedExperts: 3).Forecast!.Value;

        Assert.True(forecast.ProbabilityUp.Value > forecast.ProbabilityNeutral.Value);
        Assert.True(forecast.ProbabilityNeutral.Value > forecast.ProbabilityDown.Value);
    }

    [Fact]
    public void ProbabilitiesThatCancelToNothingBecomeUniformRatherThanFailing()
    {
        // Weighted votes can legitimately cancel to zero, and "no information" is what uniform
        // means. Refusing here would turn an ordinary aggregation outcome into a failure.
        DirectionalForecast forecast = Committee().EvaluateDirectional(
            0,
            [
                Vote(1, up: 0.0, neutral: 0.0, down: 0.0),
                Vote(2, up: 0.0, neutral: 0.0, down: 0.0),
                Vote(3, up: 0.0, neutral: 0.0, down: 0.0),
            ],
            10, 1, expectedExperts: 3).Forecast!.Value;

        Assert.Equal(1d / 3d, forecast.ProbabilityUp.Value, precision: 9);
        Assert.Equal(1d / 3d, forecast.ProbabilityNeutral.Value, precision: 9);
        Assert.Equal(1d / 3d, forecast.ProbabilityDown.Value, precision: 9);
    }

    [Fact]
    public void MissingExpertMassIsNotMistakenForConsensus()
    {
        // Section 12.3: if five experts are expected and one is valid, availability is 20%, not
        // 100%. A committee that aggregated whatever survived filtering would report a confident
        // forecast from an almost empty book.
        ForecastFamilyDecision<DirectionalForecast> decision = Committee().EvaluateDirectional(
            0,
            [Vote(1, up: 0.6, neutral: 0.3, down: 0.1)],
            10, 1, expectedExperts: 5);

        Assert.False(decision.HasForecast, decision.ReasonCode);
    }

    private static TypedForecastCommittee Committee() => new();

    private static ForecastVote<DirectionalForecast> Vote(
        int expertId, double up, double neutral, double down) =>
        new(
            expertId,
            new DirectionalForecast(
                new ForecastMetadata(
                    ExpertId: expertId,
                    InstrumentSlot: 0,
                    Type: ForecastType.DirectionalReturn,
                    Horizon: TimeSpan.FromMinutes(5),
                    GeneratedEventNs: expertId,
                    GeneratedMonotonicTicks: 1,
                    ValidUntilMonotonicTicks: 1_000,
                    SourceStateVersion: 1,
                    ModelVersion: 1,
                    Status: ForecastStatus.Valid),
                ExpectedReturnBps: 10,
                ReturnVariance: 1,
                ProbabilityUp: new Probability(up),
                ProbabilityNeutral: new Probability(neutral),
                ProbabilityDown: new Probability(down),
                CalibrationScore: 0.9),
            Weight: 1d);
}
