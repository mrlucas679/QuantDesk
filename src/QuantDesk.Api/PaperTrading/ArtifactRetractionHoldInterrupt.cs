using QuantDesk.Runtime.Execution;

namespace QuantDesk.Api.PaperTrading;

/// <summary>
/// Closes a position whose authorising research no longer stands.
///
/// The lane opens on artifact A. A is retracted or superseded, and artifact B is published — a
/// different model, a different definition, which may well also be bullish on the symbol. Before
/// binding, the hold saw a valid forecast for the right instrument and kept going, so the position
/// was held on the authority of research that never licensed it, under an exit plan belonging to a
/// publication that no longer existed.
///
/// Two cases are deliberately *not* treated as retraction.
///
/// A refreshed forecast from the same artifact keeps the position alive. Publishing a new forecast
/// each horizon is the artifact working normally, and exiting on it would close every position at
/// the first refresh.
///
/// A momentary probe failure does not close positions either. The artifact monitor fails closed
/// every ten seconds on any read error, so treating "not currently readable" as retraction would
/// liquidate on a transient file-system hiccup. Only a publication that is present and *different*
/// counts, because that is the only case where something else has genuinely taken A's place.
/// </summary>
public sealed class ArtifactRetractionHoldInterrupt(ResearchArtifactState research) : IHoldInterrupt
{
    public HoldInterrupt Evaluate(in HeldPosition position)
    {
        // No binding means no artifact ever claimed this position — the experimental mode, which is
        // honest about resting on no research. There is nothing to retract.
        if (position.Ownership is not { } ownership) return HoldInterrupt.None;

        ResearchArtifactSnapshot snapshot = research.Snapshot();

        // Unreadable is not retracted. See the class remarks.
        if (!snapshot.Ready || snapshot.Forecast is null) return HoldInterrupt.None;

        if (ownership.IsStillAuthorised(snapshot)) return HoldInterrupt.None;

        return HoldInterrupt.Now(
            $"ArtifactRetracted:held under {ownership.Describe()}, now published {snapshot.ArtifactId}");
    }
}
