using QuantDesk.Domain.Numerics;
using QuantDesk.Domain.Trading;
using QuantDesk.Runtime.Costs;

namespace QuantDesk.Api.PaperTrading;

/// <summary>
/// The cost model and research hurdle for one instrument, chosen by its asset class.
///
/// Why this is resolved per symbol rather than once at startup
/// -----------------------------------------------------------
/// Both were previously constructed from the lane's single configured symbol, at registration. That
/// worked only because the lane traded one instrument. It also hid two defects for a long time: the
/// research gate defaulted to spot-crypto costs whatever was traded, and the cost model was built
/// from the crypto fee schedule regardless, so an equity was charged an ~80 bps hurdle and a ~50 bps
/// fee. Both refused profitable trades while looking entirely reasonable.
///
/// A lane that trades several instruments cannot resolve either of these once. Even a set that is
/// all crypto today would re-acquire exactly the bug above the first time something else is added,
/// and the failure would again be silent. Asking per symbol removes the possibility rather than
/// documenting it.
/// </summary>
public sealed class AssetClassPricing(IRealisedCostSource realisedCosts, int holdingBars)
{
    // Keyed on the cost profile's own name rather than the asset class, because the profile is what
    // determines the numbers. Two routes could share an asset class and price differently -- a
    // stressed profile against a base one -- and keying on the class would silently hand the second
    // the first one's hurdle.
    private readonly Dictionary<string, CostViabilityGate> _gates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ICostModel> _costs = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    /// <summary>
    /// Whether this instrument moves enough to pay its own round trip, at its own venue's costs.
    ///
    /// Direction-neutral on purpose. The gate this replaced tested trailing momentum, which is one
    /// strategy's entry condition rather than a property of trading, and using it as a universal
    /// filter made every mean-reversion rule unreachable.
    /// </summary>
    public CostViabilityGate ViabilityFor(OpportunityRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        lock (_sync)
        {
            if (!_gates.TryGetValue(route.Costs.AssetClass, out CostViabilityGate? gate))
            {
                gate = new CostViabilityGate(route.Costs, holdingBars);
                _gates[route.Costs.AssetClass] = gate;
            }

            return gate;
        }
    }

    /// <summary>
    /// The cost charged to this instrument, floored by what round trips of this size actually cost.
    ///
    /// The measured floor is shared across asset classes because the dataset itself is keyed by
    /// notional band rather than by instrument; where it does not cover a size it returns nothing
    /// and the modelled figure stands, which is the designed fallback.
    /// </summary>
    public ICostModel CostsFor(OpportunityRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        lock (_sync)
        {
            if (!_costs.TryGetValue(route.Costs.AssetClass, out ICostModel? model))
            {
                model = new MeasuredCostFloor(Modelled(route), realisedCosts);
                _costs[route.Costs.AssetClass] = model;
            }

            return model;
        }
    }

    private static ICostModel Modelled(OpportunityRoute route)
    {
        var fee = new BasisPoints((double)route.Costs.RoundTripFeeBps);
        var slippage = new BasisPoints((double)route.Costs.SlippageAllowanceBps);

        return route.AssetClass switch
        {
            TradedAssetClass.UsEquity => new EquityCostModel(fee, slippage),
            TradedAssetClass.UsEquityOption => new OptionCostModel(fee, slippage),
            _ => new CryptoCostModel(fee, slippage),
        };
    }
}
