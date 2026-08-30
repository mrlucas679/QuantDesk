namespace QuantDesk.Domain.Runtime;

/// <summary>Immutable evidence declaration required before experimental paper execution.</summary>
public sealed record ExperimentalPaperAuthorization(
    string ExperimentId,
    string HypothesisId,
    string StrategyVersion,
    string Symbol,
    DateTimeOffset RegisteredAt,
    string EvidenceReference,
    bool LeakageSanityPassed,
    bool ReplaySanityPassed)
{
    public bool IsValidFor(string symbol) =>
        !string.IsNullOrWhiteSpace(ExperimentId) &&
        !string.IsNullOrWhiteSpace(HypothesisId) &&
        !string.IsNullOrWhiteSpace(StrategyVersion) &&
        string.Equals(Symbol, symbol, StringComparison.OrdinalIgnoreCase) &&
        RegisteredAt <= DateTimeOffset.UtcNow &&
        !string.IsNullOrWhiteSpace(EvidenceReference) &&
        LeakageSanityPassed && ReplaySanityPassed;
}
