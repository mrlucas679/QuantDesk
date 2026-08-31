using QuantDesk.Domain.Options;
using QuantDesk.Domain.Trading;

namespace QuantDesk.Domain.Execution;

public sealed record MultiLegExecutionLeg(
    string Symbol,
    int RatioQuantity,
    OrderSide Side,
    PositionIntent PositionIntent);

public sealed record MultiLegExecutionCommand(
    string ClientOrderId,
    int Quantity,
    ExecutionOrderType OrderType,
    ExecutionTimeInForce TimeInForce,
    decimal? LimitPrice,
    IReadOnlyList<MultiLegExecutionLeg> Legs)
{
    public bool IsValid()
    {
        if (string.IsNullOrWhiteSpace(ClientOrderId) || ClientOrderId.Length > 64 ||
            Quantity <= 0 || Legs.Count < 2 ||
            OrderType is not (ExecutionOrderType.Market or ExecutionOrderType.Limit) ||
            TimeInForce != ExecutionTimeInForce.Day ||
            (OrderType == ExecutionOrderType.Limit) != (LimitPrice is > 0))
            return false;
        var parsed = new List<OccOptionSymbol>(Legs.Count);
        foreach (MultiLegExecutionLeg leg in Legs)
        {
            if (leg.RatioQuantity <= 0 ||
                leg.PositionIntent is not (PositionIntent.BuyToOpen or PositionIntent.BuyToClose or
                    PositionIntent.SellToOpen or PositionIntent.SellToClose) ||
                !OccOptionSymbol.TryParse(leg.Symbol, out OccOptionSymbol? symbol) || symbol is null)
                return false;
            parsed.Add(symbol);
        }
        if (parsed.Select(item => item.Underlying).Distinct(StringComparer.Ordinal).Count() != 1)
            return false;
        return GreatestCommonDivisor(Legs.Select(leg => leg.RatioQuantity)) == 1;
    }

    private static int GreatestCommonDivisor(IEnumerable<int> values) =>
        values.Aggregate(GreatestCommonDivisor);

    private static int GreatestCommonDivisor(int left, int right)
    {
        while (right != 0) (left, right) = (right, left % right);
        return Math.Abs(left);
    }
}

public interface IMultiLegBrokerExecutionGateway
{
    bool IsPaperEnvironment => false;

    Task<BrokerSubmitResult> SubmitMultiLegAsync(
        MultiLegExecutionCommand command,
        CancellationToken cancellationToken);

    Task<BrokerOrderSnapshot?> FindByClientOrderIdAsync(
        string clientOrderId,
        CancellationToken cancellationToken);
}
