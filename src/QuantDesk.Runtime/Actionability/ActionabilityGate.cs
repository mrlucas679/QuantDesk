using QuantDesk.Domain.Portfolio;
using QuantDesk.Domain.Numerics;
using QuantDesk.Domain.Strategies;
using QuantDesk.Runtime.State;

namespace QuantDesk.Runtime.Actionability;

public enum ActionabilityReason
{
    Actionable,
    StaleQuote,
    SpreadTooWide,
    NegativeNetEdge,
    InsufficientLiquidity
}

public readonly record struct ActionabilityAssessment(bool Actionable, ActionabilityReason Reason);

public sealed class ActionabilityGate(double maximumRelativeSpread, Usd minimumNetEdge)
{
    /// <param name="explorationBudgetAvailable">
    /// Whether a bounded budget exists to buy evidence about a rule with no expected edge.
    ///
    /// The net-edge test lives in two places -- here and in the risk governor -- and teaching only
    /// the governor about the budget left this one refusing every exploration candidate before the
    /// governor ever saw it. The lane reported NegativeNetEdge with the budget switched on and
    /// nothing traded, which looked exactly like the budget not working.
    ///
    /// The quote and spread checks are unaffected. A budget buys evidence about a rule; it does not
    /// buy permission to trade on a stale price or into a spread that has blown out.
    /// </param>
    public ActionabilityAssessment Evaluate(
        in TradeCandidate candidate,
        in CostEstimate costs,
        in InstrumentSnapshot market,
        bool explorationBudgetAvailable = false)
    {
        if (market.QuoteQuality != Domain.Runtime.DataQuality.Healthy)
            return new(false, ActionabilityReason.StaleQuote);
        if (market.RelativeSpread > maximumRelativeSpread)
            return new(false, ActionabilityReason.SpreadTooWide);
        if (!explorationBudgetAvailable && candidate.GrossExpectedPnl - costs.Total < minimumNetEdge)
            return new(false, ActionabilityReason.NegativeNetEdge);
        return new(true, ActionabilityReason.Actionable);
    }
}
