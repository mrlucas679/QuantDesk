namespace QuantDesk.Domain.Runtime;

/// <summary>
/// Immutable evidence declaration required before experimental paper execution.
///
/// It names every instrument the experiment covers, not one. A lane trading several symbols under
/// an authorization naming only the first would be running the other instruments outside any
/// recorded declaration -- precisely the unrecorded scope creep this record exists to prevent, and
/// invisible because the check would still pass on the symbol it did name.
/// </summary>
public sealed record ExperimentalPaperAuthorization(
    string ExperimentId,
    string HypothesisId,
    string StrategyVersion,
    IReadOnlyList<string> Symbols,
    DateTimeOffset RegisteredAt,
    string EvidenceReference,
    bool LeakageSanityPassed,
    bool ReplaySanityPassed)
{
    public bool IsValidFor(string symbol) =>
        !string.IsNullOrWhiteSpace(ExperimentId) &&
        !string.IsNullOrWhiteSpace(HypothesisId) &&
        !string.IsNullOrWhiteSpace(StrategyVersion) &&
        Symbols.Contains(symbol, StringComparer.OrdinalIgnoreCase) &&
        RegisteredAt <= DateTimeOffset.UtcNow &&
        !string.IsNullOrWhiteSpace(EvidenceReference) &&
        LeakageSanityPassed && ReplaySanityPassed;
}
