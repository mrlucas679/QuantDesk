using QuantDesk.Domain.Forecasts;

namespace QuantDesk.Domain.Experts;

public readonly record struct ExpertVote(int ExpertId, DirectionalForecast Forecast, double Weight);

/// <summary>A family-preserving vote. The forecast remains typed rather than becoming an alpha score.</summary>
public readonly record struct ForecastVote<TForecast>(int ExpertId, TForecast Forecast, double Weight)
    where TForecast : struct, ITypedForecast;

public readonly record struct ExpertAvailability(
    int Expected,
    int Valid,
    int Abstain,
    int Stale,
    int Failed,
    int Invalid)
{
    public double Ratio => Expected <= 0 ? 0 : (double)Valid / Expected;
}

public readonly record struct ForecastFamilyDecision<TForecast>(
    ForecastType Family,
    TForecast? Forecast,
    ExpertAvailability Availability,
    string ReasonCode,
    IReadOnlyList<int> SupportingExperts)
    where TForecast : struct, ITypedForecast
{
    public bool HasForecast => Forecast.HasValue;
}

/// <summary>Explicitly distinguishes missing evidence from contradictory valid evidence.</summary>
public enum CommitteeVerdict { Consensus, Abstain, Uncertain }

public readonly record struct CommitteeDecision(
    int InstrumentSlot,
    double ExpectedReturnBps,
    double AgreementScore,
    bool Actionable,
    string ReasonCode,
    IReadOnlyList<int> SupportingExperts)
{
    public CommitteeVerdict Verdict { get; init; } = Actionable
        ? CommitteeVerdict.Consensus
        : CommitteeVerdict.Abstain;
}
