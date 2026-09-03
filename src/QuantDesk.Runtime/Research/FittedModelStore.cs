using QuantDesk.Domain.Contracts;
using QuantDesk.Runtime.Experts;

namespace QuantDesk.Runtime.Research;

/// <summary>What the runtime currently has fitted, and what it refused.</summary>
public interface IFittedModelSource
{
    /// <summary>The HAR variance model, or an unfitted one that forecasts nothing.</summary>
    HarVarianceModel Har { get; }

    /// <summary>The GARCH conditional-variance model, or an unfitted one.</summary>
    GarchVarianceModel Garch { get; }
}

/// <summary>One family's outcome on the last load attempt.</summary>
/// <param name="Family">har, garch, and so on.</param>
/// <param name="Loaded">Whether it is now driving a forecast.</param>
/// <param name="Rejection">Why not, when it is not.</param>
/// <param name="ArtifactId">Which artifact, so a decision traces to a fit.</param>
/// <param name="ProducerLibrary">Which library produced it, and at which version.</param>
public sealed record FittedModelStatus(
    string Family,
    bool Loaded,
    string Rejection,
    string? ArtifactId,
    string? ProducerLibrary);

/// <summary>
/// Holds the models the research plane published, once they have proved they load.
///
/// Why a store rather than constructor injection
/// ---------------------------------------------
/// The experts are singletons built when the host starts. The artifacts arrive later, are replaced
/// while the process runs, and may be refused. An expert given its model at construction would hold
/// whatever existed at boot forever -- which, on a fresh volume, is nothing, and that is exactly the
/// state the volatility expert has been in: registered, reachable, and permanently unfitted.
///
/// Replacement is whole, never partial
/// -----------------------------------
/// A refused artifact leaves the previous one in place rather than clearing it. The alternative --
/// dropping to unfitted on any load failure -- turns a malformed file written by a half-finished
/// research cycle into a silent change of behaviour on the trading path. Refusing to adopt is the
/// conservative move; refusing to keep what already worked is not.
/// </summary>
public sealed class FittedModelStore : IFittedModelSource
{
    private readonly Lock _gate = new();
    private HarVarianceModel _har = HarVarianceModel.Unfitted();
    private GarchVarianceModel _garch = GarchVarianceModel.Unfitted();
    private IReadOnlyList<FittedModelStatus> _status = [];

    public HarVarianceModel Har
    {
        get { lock (_gate) return _har; }
    }

    public GarchVarianceModel Garch
    {
        get { lock (_gate) return _garch; }
    }

    /// <summary>What happened on the last attempt, for the status surface.</summary>
    public IReadOnlyList<FittedModelStatus> Status
    {
        get { lock (_gate) return _status; }
    }

    /// <summary>Adopts a HAR model that has already been validated and reproduced its parity.</summary>
    public void Adopt(HarVarianceModel har)
    {
        ArgumentNullException.ThrowIfNull(har);
        if (!har.IsFitted) return;
        lock (_gate) _har = har;
    }

    /// <summary>Adopts a GARCH model that has already been validated and reproduced its parity.</summary>
    public void Adopt(GarchVarianceModel garch)
    {
        ArgumentNullException.ThrowIfNull(garch);
        if (!garch.IsFitted) return;
        lock (_gate) _garch = garch;
    }

    /// <summary>Records what the last load produced, adopted or refused.</summary>
    public void Record(IReadOnlyList<FittedModelStatus> status)
    {
        ArgumentNullException.ThrowIfNull(status);
        lock (_gate) _status = status;
    }

    /// <summary>A source holding exactly these models, for a caller that has them already.</summary>
    public static FittedModelStore Of(
        HarVarianceModel? har = null, GarchVarianceModel? garch = null)
    {
        var store = new FittedModelStore();
        if (har is not null) store.Adopt(har);
        if (garch is not null) store.Adopt(garch);
        return store;
    }
}
