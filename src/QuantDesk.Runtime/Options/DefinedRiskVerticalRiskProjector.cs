using QuantDesk.Domain.Numerics;
using QuantDesk.Domain.Options;
using QuantDesk.Domain.Runtime;
using QuantDesk.Domain.Strategies;
using QuantDesk.Domain.Trading;

namespace QuantDesk.Runtime.Options;

/// <summary>Projects authenticated per-leg Greeks into the common risk-governor exposure model.</summary>
public static class DefinedRiskVerticalRiskProjector
{
    public static bool TryProject(
        in TradeCandidate directionalCandidate,
        MultiLegOptionCandidate vertical,
        decimal underlyingPrice,
        IReadOnlyDictionary<int, int> multipliers,
        IReadOnlyDictionary<int, OptionRiskSnapshot> snapshots,
        out TradeCandidate projected)
    {
        ArgumentNullException.ThrowIfNull(vertical);
        ArgumentNullException.ThrowIfNull(multipliers);
        ArgumentNullException.ThrowIfNull(snapshots);
        projected = default;
        if (underlyingPrice <= 0 || vertical.DefinedMaximumLoss.Value <= 0 || vertical.Legs.Count < 2)
            return false;

        double delta = 0;
        double gamma = 0;
        double vega = 0;
        double theta = 0;
        foreach (OptionLegCandidate leg in vertical.Legs)
        {
            if (!multipliers.TryGetValue(leg.ContractSlot, out int multiplier) || multiplier <= 0 ||
                !snapshots.TryGetValue(leg.ContractSlot, out OptionRiskSnapshot snapshot) ||
                snapshot.Quality != DataQuality.Healthy || snapshot.ImpliedVolatility <= 0 ||
                !double.IsFinite(snapshot.Delta) || !double.IsFinite(snapshot.Gamma) ||
                !double.IsFinite(snapshot.Vega) || !double.IsFinite(snapshot.Theta))
                return false;
            double sign = leg.Side == OrderSide.Buy ? 1d : -1d;
            double units = sign * leg.Ratio * multiplier;
            delta += units * snapshot.Delta * (double)underlyingPrice;
            gamma += units * snapshot.Gamma * Math.Pow((double)underlyingPrice * .01d, 2);
            vega += units * snapshot.Vega * .01d;
            theta += units * snapshot.Theta;
        }

        Usd maximumLoss = vertical.DefinedMaximumLoss;
        var exposure = new EconomicExposure(
            new Usd(maximumLoss.Value), delta, gamma, vega, theta,
            0, 0, 0, maximumLoss, maximumLoss, 0);
        projected = directionalCandidate with
        {
            StrategyId = vertical.StrategyId,
            RiskBasis = RiskBasis.DefinedMaximumLoss,
            EstimatedStressLoss = maximumLoss,
            Exposure = exposure,
            ManagementPlan = vertical.ManagementPlan
        };
        return true;
    }
}
