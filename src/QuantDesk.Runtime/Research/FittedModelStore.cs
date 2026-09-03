using QuantDesk.Domain.Contracts;
using QuantDesk.Runtime.Experts;

namespace QuantDesk.Runtime.Research;

/// <summary>What the runtime currently has fitted, and what it refused.</summary>
/// <remarks>
/// Every lookup names an instrument, and that is the whole point of the interface's shape. Asking
/// for "the HAR model" was answerable only because there was one, fitted on BTC/USD and handed to
/// SPY, QQQ, IWM and DIA without anything being in a position to object. A question that cannot be
/// asked without saying which instrument it is about cannot be answered for the wrong one.
/// </remarks>
public interface IFittedModelSource
{
    /// <summary>
    /// The HAR variance model fitted for this instrument, or an unfitted one that forecasts nothing.
    /// </summary>
    HarVarianceModel Har(string symbol, int barDurationMinutes);

    /// <summary>The GARCH conditional-variance model for this instrument, or an unfitted one.</summary>
    GarchVarianceModel Garch(string symbol, int barDurationMinutes);
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
    private readonly List<Fitted<HarVarianceModel>> _har = [];
    private readonly List<Fitted<GarchVarianceModel>> _garch = [];
    private IReadOnlyList<FittedModelStatus> _status = [];

    /// <summary>A model together with the domain it was fitted on, which is the only pair that means anything.</summary>
    private readonly record struct Fitted<TModel>(TModel Model, ExpertSupportDomain Domain);

    public HarVarianceModel Har(string symbol, int barDurationMinutes)
    {
        lock (_gate)
        {
            foreach (Fitted<HarVarianceModel> fitted in _har)
            {
                if (fitted.Domain.Supports(symbol, barDurationMinutes)) return fitted.Model;
            }
        }

        return HarVarianceModel.Unfitted();
    }

    public GarchVarianceModel Garch(string symbol, int barDurationMinutes)
    {
        lock (_gate)
        {
            foreach (Fitted<GarchVarianceModel> fitted in _garch)
            {
                if (fitted.Domain.Supports(symbol, barDurationMinutes)) return fitted.Model;
            }
        }

        return GarchVarianceModel.Unfitted();
    }

    /// <summary>What happened on the last attempt, for the status surface.</summary>
    public IReadOnlyList<FittedModelStatus> Status
    {
        get { lock (_gate) return _status; }
    }

    /// <summary>
    /// Adopts a HAR model that has validated, reproduced its parity, and said what it covers.
    ///
    /// An undeclared domain is refused rather than adopted globally. That is the behaviour change:
    /// an artifact that does not say what it was fitted on is not a model for everything, it is a
    /// model whose reach was never established.
    /// </summary>
    public bool Adopt(HarVarianceModel har, ExpertSupportDomain domain)
    {
        ArgumentNullException.ThrowIfNull(har);
        ArgumentNullException.ThrowIfNull(domain);
        if (!har.IsFitted || !domain.IsDeclared) return false;
        lock (_gate) Replace(_har, new Fitted<HarVarianceModel>(har, domain));
        return true;
    }

    /// <summary>Adopts a GARCH model that has validated, reproduced its parity, and said what it covers.</summary>
    public bool Adopt(GarchVarianceModel garch, ExpertSupportDomain domain)
    {
        ArgumentNullException.ThrowIfNull(garch);
        ArgumentNullException.ThrowIfNull(domain);
        if (!garch.IsFitted || !domain.IsDeclared) return false;
        lock (_gate) Replace(_garch, new Fitted<GarchVarianceModel>(garch, domain));
        return true;
    }

    /// <summary>
    /// Supersedes the entry covering the same ground, and keeps every other one.
    ///
    /// A per-symbol bank has to hold several models at once, so a fresh fit for BTC/USD must not
    /// evict the one for SPY -- which is what a single field did by construction. Same domain
    /// replaces; different domain is added alongside.
    /// </summary>
    private static void Replace<TModel>(List<Fitted<TModel>> bank, Fitted<TModel> arrival)
    {
        bank.RemoveAll(existing => existing.Domain == arrival.Domain);
        bank.Add(arrival);
    }

    /// <summary>Records what the last load produced, adopted or refused.</summary>
    public void Record(IReadOnlyList<FittedModelStatus> status)
    {
        ArgumentNullException.ThrowIfNull(status);
        lock (_gate) _status = status;
    }

    /// <summary>Every domain this store currently holds a model for, for the status surface.</summary>
    public IReadOnlyList<ExpertSupportDomain> Domains
    {
        get
        {
            lock (_gate)
            {
                return
                [
                    .. _har.Select(fitted => fitted.Domain)
                        .Concat(_garch.Select(fitted => fitted.Domain))
                        .Distinct()
                ];
            }
        }
    }

    /// <summary>A source holding exactly these models over one domain, for a caller that has them.</summary>
    public static FittedModelStore Of(
        ExpertSupportDomain domain,
        HarVarianceModel? har = null,
        GarchVarianceModel? garch = null)
    {
        var store = new FittedModelStore();
        if (har is not null) store.Adopt(har, domain);
        if (garch is not null) store.Adopt(garch, domain);
        return store;
    }
}
