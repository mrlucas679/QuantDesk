using QuantDesk.Alpaca.Mapping;
using QuantDesk.Domain.Contracts;
using QuantDesk.Domain.Execution;
using QuantDesk.Domain.Options;
using QuantDesk.Domain.Risk;
using QuantDesk.Domain.Trading;
using QuantDesk.Runtime.Execution;

namespace QuantDesk.Api.PaperTrading;

/// <summary>
/// Binds a research-approved debit vertical to the durable MLeg lifecycle. It is intentionally a
/// narrow boundary: no caller can substitute contracts, downside, or exit economics after the
/// artifact and risk decision have been approved.
/// </summary>
public sealed class DefinedRiskVerticalLifecycleService(
    MultiLegExecutionLifecycle lifecycle,
    IInstrumentSymbolResolver symbols)
{
    public bool TryReserve(
        string executionId,
        MultiLegOptionCandidate candidate,
        StrategyDefinitionContract definition,
        RiskDecision risk)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(definition);
        if (!risk.Approved || definition.ExecutionKind != StrategyExecutionKind.DefinedRiskVertical ||
            definition.OptionVertical is not { } policy || !policy.IsValid() ||
            candidate.Legs.Count < 2 || candidate.DefinedMaximumLoss.Value <= 0 ||
            candidate.DefinedMaximumLoss.Value > policy.MaximumDefinedLoss ||
            candidate.DefinedMaximumLoss.Value > risk.RequiredRiskReservation.Value ||
            candidate.NetLimitPrice <= 0 || candidate.ManagementPlan.MaximumHoldingPeriod <= TimeSpan.Zero)
            return false;

        var legs = new List<MultiLegExecutionLeg>(candidate.Legs.Count);
        foreach (OptionLegCandidate leg in candidate.Legs)
        {
            if (!symbols.TryResolve(leg.ContractSlot, out string symbol) ||
                !OccOptionSymbol.TryParse(symbol, out OccOptionSymbol? parsed) || parsed is null)
                return false;
            PositionIntent intent = leg.Intent switch
            {
                PositionIntent.Open => leg.Side == OrderSide.Buy
                    ? PositionIntent.BuyToOpen
                    : PositionIntent.SellToOpen,
                PositionIntent.BuyToOpen or PositionIntent.SellToOpen => leg.Intent,
                _ => default
            };
            if (intent == default) return false;
            legs.Add(new MultiLegExecutionLeg(parsed.BrokerSymbol, leg.Ratio, leg.Side, intent));
        }

        decimal exitLimit = decimal.Round(
            candidate.NetLimitPrice * policy.ExitLimitFraction, 2, MidpointRounding.ToPositiveInfinity);
        return lifecycle.TryReserve(
            executionId,
            candidate.StrategyId,
            quantity: 1,
            entryLimitPrice: candidate.NetLimitPrice,
            exitLimitPrice: exitLimit,
            definedMaximumLoss: candidate.DefinedMaximumLoss.Value,
            maximumHoldingPeriod: candidate.ManagementPlan.MaximumHoldingPeriod,
            entryLegs: legs);
    }
}
