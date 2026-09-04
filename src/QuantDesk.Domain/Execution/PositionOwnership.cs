namespace QuantDesk.Domain.Execution;

/// <summary>
/// The research publication that authorised an open position, captured when it was opened.
///
/// The defect this closes
/// ----------------------
/// The lane decided whether a position's thesis still held by asking whether a verified forecast
/// currently existed for the symbol, was of the right family, and pointed the right way. It never
/// asked whether that forecast came from the artifact that had authorised the position.
///
/// So: the lane opens on artifact A. A is then retracted — found overfit, or simply superseded —
/// and artifact B is published, a different model with a different definition that also happens to
/// be bullish on the symbol. The check passes, and the position goes on being held on the authority
/// of research that never licensed it, under an exit plan belonging to a publication that no longer
/// exists. Nobody is left who can say why the position is open.
///
/// Binding makes the question answerable. A position names the artifact, model version, and
/// artifact hash that licensed it, and holding is justified only while that exact publication still
/// stands.
/// </summary>
/// <param name="ArtifactId">Identity of the model artifact that licensed the position.</param>
/// <param name="ModelVersion">Version within that artifact, from the forecast that triggered entry.</param>
/// <param name="ArtifactHash">Content hash, so a re-publication under the same ID is still detected.</param>
/// <param name="StrategyFamily">The family the artifact declared.</param>
/// <param name="BoundAt">When the binding was taken.</param>
public sealed record PositionOwnership(
    string ArtifactId,
    string ModelVersion,
    string ArtifactHash,
    string StrategyFamily,
    DateTimeOffset BoundAt)
{
    public bool IsValid() => !string.IsNullOrWhiteSpace(ArtifactId)
        && !string.IsNullOrWhiteSpace(ModelVersion)
        && !string.IsNullOrWhiteSpace(ArtifactHash);

    /// <summary>
    /// Whether a currently published artifact is the same one that licensed this position.
    ///
    /// A refreshed forecast from the same artifact keeps the thesis alive — re-publishing a forecast
    /// every horizon is what an artifact is for. A different artifact ID, a different model version,
    /// or the same ID republished with different content all mean the licence has changed, and the
    /// position is no longer covered by the research that opened it.
    /// </summary>
    public bool Matches(string? artifactId, string? modelVersion, string? artifactHash) =>
        string.Equals(artifactId, ArtifactId, StringComparison.Ordinal)
        && string.Equals(modelVersion, ModelVersion, StringComparison.Ordinal)
        && string.Equals(artifactHash, ArtifactHash, StringComparison.Ordinal);

    /// <summary>An operator-readable line naming what licensed this position.</summary>
    public string Describe() =>
        $"{ArtifactId}@{ModelVersion} ({StrategyFamily}, hash {Shorten(ArtifactHash)}) bound {BoundAt:u}";

    private static string Shorten(string hash) => hash.Length <= 12 ? hash : hash[..12];
}
