using QuantDesk.Domain.Execution;
using QuantDesk.Domain.Portfolio;

namespace QuantDesk.Runtime.Execution;

public sealed class TradeUpdateProcessor(Portfolio.PortfolioLedger portfolioLedger)
{
    public bool ApplyFill(BrokerTradeUpdate update, int instrumentSlot, QuantDesk.Domain.Trading.OrderSide side)
    {
        if (update.Kind is not (BrokerTradeUpdateKind.Fill or BrokerTradeUpdateKind.PartialFill) ||
            update.FilledQuantity <= 0 || update.FilledPrice <= 0 || update.EventUnixNanoseconds <= 0)
            return false;
        string fillId = $"{update.BrokerOrderId}:{update.EventUnixNanoseconds}:{update.FilledQuantity}:{update.FilledPrice}";
        return portfolioLedger.ApplyFill(new NormalizedFill(
            update.ClientOrderId,
            update.BrokerOrderId,
            instrumentSlot,
            side,
            update.FilledQuantity,
            update.FilledPrice,
            update.EventUnixNanoseconds,
            fillId));
    }
}
