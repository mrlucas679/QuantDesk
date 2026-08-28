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
    public ActionabilityAssessment Evaluate(
        in TradeCandidate candidate,
        in CostEstimate costs,
        in InstrumentSnapshot market)
    {
        if (market.QuoteQuality != Domain.Runtime.DataQuality.Healthy)
            return new(false, ActionabilityReason.StaleQuote);
        if (market.RelativeSpread > maximumRelativeSpread)
            return new(false, ActionabilityReason.SpreadTooWide);
        if (candidate.GrossExpectedPnl - costs.Total < minimumNetEdge)
            return new(false, ActionabilityReason.NegativeNetEdge);
        return new(true, ActionabilityReason.Actionable);
    }
}
