using QuantDesk.Domain.Experts;
using QuantDesk.Domain.Forecasts;
using QuantDesk.Domain.Numerics;

namespace QuantDesk.Runtime.Experts;

/// <summary>
/// Aggregates only forecasts with the same units and horizon. No forecast family is converted to a
/// universal score, so volatility, liquidity and context can never accidentally become direction.
/// </summary>
public sealed class TypedForecastCommittee(
    double minimumAvailabilityRatio = 0.5,
    double minimumCalibrationScore = 0.5)
{
    public ForecastFamilyDecision<DirectionalForecast> EvaluateDirectional(
        int instrumentSlot,
        IReadOnlyList<ForecastVote<DirectionalForecast>> votes,
        long nowMonotonicTicks,
        long sourceStateVersion,
        int expectedExperts = 0)
    {
        Selection<DirectionalForecast> selection = Select(
            ForecastType.DirectionalReturn, instrumentSlot, votes, nowMonotonicTicks,
            sourceStateVersion, expectedExperts);
        if (!selection.CanAggregate)
            return Refused<DirectionalForecast>(ForecastType.DirectionalReturn, selection);

        double calibration = selection.Weighted(forecast => forecast.CalibrationScore);
        if (calibration < minimumCalibrationScore)
            return Refused<DirectionalForecast>(ForecastType.DirectionalReturn, selection, "calibration_below_threshold");

        double down = selection.Weighted(forecast => forecast.ProbabilityDown.Value);
        double neutral = selection.Weighted(forecast => forecast.ProbabilityNeutral.Value);
        double up = selection.Weighted(forecast => forecast.ProbabilityUp.Value);
        double[] directional = Normalized(down, neutral, up);
        DirectionalForecast forecast = new(
            selection.AggregateMetadata(),
            selection.Weighted(value => value.ExpectedReturnBps),
            Math.Max(0, selection.Weighted(value => value.ReturnVariance)),
            new Probability(directional[2]), new Probability(directional[1]),
            new Probability(directional[0]), calibration);
        return Accepted(ForecastType.DirectionalReturn, forecast, selection);
    }

    public ForecastFamilyDecision<VolatilityForecast> EvaluateVolatility(
        int instrumentSlot,
        IReadOnlyList<ForecastVote<VolatilityForecast>> votes,
        long nowMonotonicTicks,
        long sourceStateVersion,
        int expectedExperts = 0)
    {
        Selection<VolatilityForecast> selection = Select(
            ForecastType.RealizedVolatility, instrumentSlot, votes, nowMonotonicTicks,
            sourceStateVersion, expectedExperts);
        if (!selection.CanAggregate)
            return Refused<VolatilityForecast>(ForecastType.RealizedVolatility, selection);

        VolatilityForecast forecast = new(
            selection.AggregateMetadata(),
            Math.Max(0, selection.Weighted(value => value.ExpectedRealizedVariance)),
            Math.Max(0, selection.Weighted(value => value.ExpectedAnnualizedVolatility)),
            Math.Max(0, selection.Weighted(value => value.ForecastVariance)),
            selection.Weighted(value => value.CalibrationScore));
        return Accepted(ForecastType.RealizedVolatility, forecast, selection);
    }

    public ForecastFamilyDecision<RegimeForecast> EvaluateRegime(
        int instrumentSlot,
        IReadOnlyList<ForecastVote<RegimeForecast>> votes,
        long nowMonotonicTicks,
        long sourceStateVersion,
        int expectedExperts = 0)
    {
        Selection<RegimeForecast> selection = Select(
            ForecastType.Regime, instrumentSlot, votes, nowMonotonicTicks,
            sourceStateVersion, expectedExperts);
        if (!selection.CanAggregate) return Refused<RegimeForecast>(ForecastType.Regime, selection);

        double low = selection.Weighted(value => value.LowVolTrend.Value);
        double high = selection.Weighted(value => value.HighVolTrend.Value);
        double range = selection.Weighted(value => value.Range.Value);
        double stress = selection.Weighted(value => value.Stress.Value);
        double eventProbability = selection.Weighted(value => value.Event.Value);
        double[] regime = Normalized(low, high, range, stress, eventProbability);
        RegimeForecast forecast = new(
            selection.AggregateMetadata(), new Probability(regime[0]), new Probability(regime[1]),
            new Probability(regime[2]), new Probability(regime[3]), new Probability(regime[4]),
            selection.Weighted(value => value.CalibrationScore));
        return Accepted(ForecastType.Regime, forecast, selection);
    }

    public ForecastFamilyDecision<RelativeValueForecast> EvaluateRelativeValue(
        int instrumentSlot,
        IReadOnlyList<ForecastVote<RelativeValueForecast>> votes,
        long nowMonotonicTicks,
        long sourceStateVersion,
        int expectedExperts = 0)
    {
        Selection<RelativeValueForecast> selection = Select(
            ForecastType.RelativeValue, instrumentSlot, votes, nowMonotonicTicks,
            sourceStateVersion, expectedExperts);
        if (!selection.CanAggregate) return Refused<RelativeValueForecast>(ForecastType.RelativeValue, selection);

        int secondSlot = selection.Valid[0].Forecast.SecondInstrumentSlot;
        if (selection.Valid.Any(vote => vote.Forecast.SecondInstrumentSlot != secondSlot))
            return Refused<RelativeValueForecast>(ForecastType.RelativeValue, selection, "incomparable_second_instrument");

        RelativeValueForecast forecast = new(
            selection.AggregateMetadata(), secondSlot,
            selection.Weighted(value => value.ExpectedResidualChangeBps),
            Math.Max(0, selection.Weighted(value => value.ResidualVariance)),
            selection.Weighted(value => value.HedgeRatio),
            Math.Clamp(selection.Weighted(value => value.RelationshipStability), 0, 1));
        return Accepted(ForecastType.RelativeValue, forecast, selection);
    }

    public ForecastFamilyDecision<JumpRiskForecast> EvaluateJumpRisk(
        int instrumentSlot,
        IReadOnlyList<ForecastVote<JumpRiskForecast>> votes,
        long nowMonotonicTicks,
        long sourceStateVersion,
        int expectedExperts = 0)
    {
        Selection<JumpRiskForecast> selection = Select(
            ForecastType.JumpRisk, instrumentSlot, votes, nowMonotonicTicks,
            sourceStateVersion, expectedExperts);
        if (!selection.CanAggregate) return Refused<JumpRiskForecast>(ForecastType.JumpRisk, selection);

        JumpRiskForecast forecast = new(
            selection.AggregateMetadata(),
            new Probability(Math.Clamp(selection.Weighted(value => value.JumpProbability.Value), 0, 1)),
            Math.Max(0, selection.Weighted(value => value.ExpectedAbsoluteMoveBps)),
            Math.Max(0, selection.Weighted(value => value.ExpectedDownsideMoveBps)),
            selection.Weighted(value => value.CalibrationScore));
        return Accepted(ForecastType.JumpRisk, forecast, selection);
    }

    public ForecastFamilyDecision<LiquidityCostForecast> EvaluateLiquidity(
        int instrumentSlot,
        IReadOnlyList<ForecastVote<LiquidityCostForecast>> votes,
        long nowMonotonicTicks,
        long sourceStateVersion,
        int expectedExperts = 0)
    {
        Selection<LiquidityCostForecast> selection = Select(
            ForecastType.LiquidityCost, instrumentSlot, votes, nowMonotonicTicks,
            sourceStateVersion, expectedExperts);
        if (!selection.CanAggregate) return Refused<LiquidityCostForecast>(ForecastType.LiquidityCost, selection);

        LiquidityCostForecast forecast = new(
            selection.AggregateMetadata(),
            Math.Max(0, selection.Weighted(value => value.ExpectedSpreadBps)),
            Math.Max(0, selection.Weighted(value => value.ExpectedSlippageBps)),
            new Probability(Math.Clamp(selection.Weighted(value => value.FillProbability.Value), 0, 1)),
            new Usd((decimal)Math.Max(0, selection.Weighted(value => (double)value.EstimatedSafeNotional.Value))),
            selection.Weighted(value => value.CalibrationScore));
        return Accepted(ForecastType.LiquidityCost, forecast, selection);
    }

    public ForecastFamilyDecision<OptionSurfaceForecast> EvaluateOptionSurface(
        int instrumentSlot,
        IReadOnlyList<ForecastVote<OptionSurfaceForecast>> votes,
        long nowMonotonicTicks,
        long sourceStateVersion,
        int expectedExperts = 0)
    {
        Selection<OptionSurfaceForecast> selection = Select(
            ForecastType.OptionSurface, instrumentSlot, votes, nowMonotonicTicks,
            sourceStateVersion, expectedExperts);
        if (!selection.CanAggregate) return Refused<OptionSurfaceForecast>(ForecastType.OptionSurface, selection);

        OptionSurfaceForecast forecast = new(
            selection.AggregateMetadata(),
            Math.Max(0, selection.Weighted(value => value.AtmImpliedVariance)),
            Math.Max(0, selection.Weighted(value => value.ExpectedRealizedVariance)),
            selection.Weighted(value => value.VarianceRiskPremium),
            selection.Weighted(value => value.Skew),
            Math.Clamp(selection.Weighted(value => value.SurfaceQuality), 0, 1),
            selection.Weighted(value => value.CalibrationScore));
        return Accepted(ForecastType.OptionSurface, forecast, selection);
    }

    public ForecastFamilyDecision<MicrostructureForecast> EvaluateMicrostructure(
        int instrumentSlot,
        IReadOnlyList<ForecastVote<MicrostructureForecast>> votes,
        long nowMonotonicTicks,
        long sourceStateVersion,
        int expectedExperts = 0)
    {
        Selection<MicrostructureForecast> selection = Select(
            ForecastType.Microstructure, instrumentSlot, votes, nowMonotonicTicks,
            sourceStateVersion, expectedExperts);
        if (!selection.CanAggregate) return Refused<MicrostructureForecast>(ForecastType.Microstructure, selection);

        MicrostructureForecast forecast = new(
            selection.AggregateMetadata(),
            Math.Clamp(selection.Weighted(value => value.OrderBookImbalance), -1, 1),
            selection.Weighted(value => value.ExpectedReturnBps),
            new Probability(Math.Clamp(selection.Weighted(value => value.FillProbability.Value), 0, 1)),
            Math.Clamp(selection.Weighted(value => value.AdverseSelectionRisk), 0, 1),
            selection.Weighted(value => value.CalibrationScore));
        return Accepted(ForecastType.Microstructure, forecast, selection);
    }

    private Selection<TForecast> Select<TForecast>(
        ForecastType family,
        int instrumentSlot,
        IReadOnlyList<ForecastVote<TForecast>> votes,
        long nowMonotonicTicks,
        long sourceStateVersion,
        int expectedExperts)
        where TForecast : struct, ITypedForecast
    {
        ArgumentNullException.ThrowIfNull(votes);
        int expected = expectedExperts > 0 ? expectedExperts : votes.Count;
        if (expected < votes.Count)
            throw new ArgumentOutOfRangeException(nameof(expectedExperts), "Expected count cannot be smaller than supplied votes.");

        List<ForecastVote<TForecast>> valid = [];
        HashSet<int> seen = [];
        int abstain = expected - votes.Count;
        int stale = 0, failed = 0, invalid = 0;
        TimeSpan? horizon = null;
        foreach (ForecastVote<TForecast> vote in votes)
        {
            ForecastMetadata metadata = vote.Forecast.Metadata;
            if (!seen.Add(vote.ExpertId) || vote.ExpertId != metadata.ExpertId ||
                metadata.Type != family || metadata.InstrumentSlot != instrumentSlot ||
                !double.IsFinite(vote.Weight) || vote.Weight <= 0)
            {
                invalid++;
                continue;
            }
            switch (metadata.Status)
            {
                case ForecastStatus.Abstain: abstain++; continue;
                case ForecastStatus.Stale: stale++; continue;
                case ForecastStatus.Failed: failed++; continue;
                case ForecastStatus.Invalid: invalid++; continue;
            }
            if (!ForecastValidity.IsFresh(metadata, nowMonotonicTicks)) { stale++; continue; }
            if (!ForecastValidity.IsCausal(metadata, sourceStateVersion)) { invalid++; continue; }
            if (horizon is not null && horizon.Value != metadata.Horizon) { invalid++; continue; }
            horizon = metadata.Horizon;
            valid.Add(vote);
        }

        ExpertAvailability availability = new(expected, valid.Count, abstain, stale, failed, invalid);
        string reason = valid.Count == 0 ? "insufficient_valid_evidence"
            : availability.Ratio < minimumAvailabilityRatio ? "insufficient_expert_availability"
            : "consensus";
        return new Selection<TForecast>(valid, availability, reason, sourceStateVersion);
    }

    private static ForecastFamilyDecision<TForecast> Accepted<TForecast>(
        ForecastType family,
        TForecast forecast,
        Selection<TForecast> selection)
        where TForecast : struct, ITypedForecast =>
        new(family, forecast, selection.Availability, "consensus", selection.ExpertIds);

    private static ForecastFamilyDecision<TForecast> Refused<TForecast>(
        ForecastType family,
        Selection<TForecast> selection,
        string? reason = null)
        where TForecast : struct, ITypedForecast =>
        new(family, null, selection.Availability, reason ?? selection.ReasonCode, selection.ExpertIds);

    /// <summary>
    /// Clamps a set of probabilities to [0,1] and rescales them to sum to one.
    ///
    /// Returns the normalised set rather than mutating the caller's variables. The previous shape
    /// took <c>params double[]</c> and wrote into it, which normalised a freshly allocated array
    /// and threw it away -- the caller's values were untouched. The call sites reached for
    /// <c>ref</c> to compensate, which does not compile with <c>params</c>, and that is the only
    /// reason the mistake was visible at all. Written the other way it would have compiled and
    /// silently published unnormalised probabilities, which &sect;26.2 lists as a financial
    /// invariant and Appendix A as a golden oracle.
    ///
    /// A set that sums to nothing becomes uniform. Weighted votes can legitimately cancel to zero,
    /// and "no information" is what uniform means; refusing here would turn an ordinary aggregation
    /// outcome into a failure.
    /// </summary>
    private static double[] Normalized(params double[] probabilities)
    {
        double[] result = new double[probabilities.Length];
        for (int index = 0; index < probabilities.Length; index++)
        {
            double value = probabilities[index];
            result[index] = double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0d;
        }

        double sum = 0d;
        foreach (double value in result) sum += value;

        if (sum <= 0)
        {
            Array.Fill(result, 1d / result.Length);
            return result;
        }

        for (int index = 0; index < result.Length; index++) result[index] /= sum;
        return result;
    }

    private sealed record Selection<TForecast>(
        IReadOnlyList<ForecastVote<TForecast>> Valid,
        ExpertAvailability Availability,
        string ReasonCode,
        long SourceStateVersion)
        where TForecast : struct, ITypedForecast
    {
        public bool CanAggregate => Valid.Count > 0 && ReasonCode == "consensus";
        public IReadOnlyList<int> ExpertIds => Valid.Select(vote => vote.ExpertId).ToArray();

        public double Weighted(Func<TForecast, double> selector)
        {
            double totalWeight = Valid.Sum(vote => vote.Weight);
            return Valid.Sum(vote => vote.Weight * selector(vote.Forecast)) / totalWeight;
        }

        public ForecastMetadata AggregateMetadata()
        {
            ForecastMetadata first = Valid[0].Forecast.Metadata;
            return first with
            {
                ExpertId = 1000 + (int)first.Type,
                GeneratedEventNs = Valid.Max(vote => vote.Forecast.Metadata.GeneratedEventNs),
                GeneratedMonotonicTicks = Valid.Max(vote => vote.Forecast.Metadata.GeneratedMonotonicTicks),
                ValidUntilMonotonicTicks = Valid.Min(vote => vote.Forecast.Metadata.ValidUntilMonotonicTicks),
                SourceStateVersion = SourceStateVersion,
                ModelVersion = Valid.Max(vote => vote.Forecast.Metadata.ModelVersion),
                Status = ForecastStatus.Valid
            };
        }
    }
}
