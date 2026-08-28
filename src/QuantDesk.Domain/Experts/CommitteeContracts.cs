using QuantDesk.Domain.Forecasts;

namespace QuantDesk.Domain.Experts;

public readonly record struct ExpertVote(int ExpertId, DirectionalForecast Forecast, double Weight);

public readonly record struct CommitteeDecision(
    int InstrumentSlot,
    double ExpectedReturnBps,
    double AgreementScore,
    bool Actionable,
    string ReasonCode,
    IReadOnlyList<int> SupportingExperts);
