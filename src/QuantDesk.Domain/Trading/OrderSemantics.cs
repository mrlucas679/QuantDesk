namespace QuantDesk.Domain.Trading;

public enum OrderSide { Buy, Sell }

public enum PositionIntent
{
    Open,
    Close,
    Reduce,
    Increase,
    BuyToOpen,
    BuyToClose,
    SellToOpen,
    SellToClose
}

public enum ExecutionOrderType { Market, Limit, StopLimit }

public enum ExecutionTimeInForce { Day, Gtc, Ioc }

public enum RiskBasis
{
    DefinedMaximumLoss,
    StressLoss,
    NotionalRisk,
    StopBasedRisk
}

