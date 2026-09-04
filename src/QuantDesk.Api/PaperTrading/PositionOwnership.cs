using QuantDesk.Domain.Execution;

namespace QuantDesk.Api.PaperTrading;

/// <summary>
/// Binds an open position to the research publication that authorised it.
///
/// The binding itself lives in the domain; this is the part that knows how to read one out of the
/// execution plane's view of published research, and how to ask whether that publication still
/// stands. See <see cref="PositionOwnership"/> for why a position has to be able to name what
/// licensed it.
/// </summary>
public static class ResearchPositionOwnership
{
    /// <summary>
    /// Binds a position to the publication that authorised it, or returns null if it cannot.
    ///
    /// Null means the snapshot could not name what licensed the trade. A position that cannot say
    /// what authorised it is exactly the state binding exists to prevent, so the caller abstains
    /// rather than opening one.
    /// </summary>
    public static PositionOwnership? Bind(ResearchArtifactSnapshot research, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(research);

        if (!research.Ready) return null;
        if (research.ArtifactId is not { } artifactId || string.IsNullOrWhiteSpace(artifactId)) return null;
        if (research.Forecast is not { } forecast) return null;
        if (string.IsNullOrWhiteSpace(forecast.ModelVersion)) return null;
        if (string.IsNullOrWhiteSpace(forecast.ArtifactHash)) return null;

        return new PositionOwnership(
            artifactId,
            forecast.ModelVersion,
            forecast.ArtifactHash,
            research.StrategyFamily ?? string.Empty,
            now);
    }

    /// <summary>Whether the publication that licensed this position still stands.</summary>
    public static bool IsStillAuthorised(this PositionOwnership ownership, ResearchArtifactSnapshot research)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        ArgumentNullException.ThrowIfNull(research);

        return research.Ready
            && research.Forecast is { } forecast
            && ownership.Matches(research.ArtifactId, forecast.ModelVersion, forecast.ArtifactHash);
    }
}
