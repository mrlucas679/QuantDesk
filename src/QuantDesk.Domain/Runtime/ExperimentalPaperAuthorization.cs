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
    /// <summary>
    /// Whether this authorization covers <paramref name="symbol"/> as of <paramref name="asOf"/>.
    ///
    /// The moment is a parameter rather than a reading taken inside. The domain project references
    /// nothing and so cannot hold a clock, but the shape is right regardless: whether an
    /// authorization has taken effect is a question about a point in time, and a method that picks
    /// the point itself cannot be replayed and cannot be tested at a boundary.
    /// </summary>
    public bool IsValidFor(string symbol, DateTimeOffset asOf) =>
        !string.IsNullOrWhiteSpace(ExperimentId) &&
        !string.IsNullOrWhiteSpace(HypothesisId) &&
        !string.IsNullOrWhiteSpace(StrategyVersion) &&
        Symbols.Contains(symbol, StringComparer.OrdinalIgnoreCase) &&
        RegisteredAt <= asOf &&
        !string.IsNullOrWhiteSpace(EvidenceReference) &&
        LeakageSanityPassed && ReplaySanityPassed;
}
