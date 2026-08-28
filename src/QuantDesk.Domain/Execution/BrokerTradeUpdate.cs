namespace QuantDesk.Domain.Execution;

public enum BrokerTradeUpdateKind { New, Fill, PartialFill, Canceled, Rejected, Expired, Unknown }

public readonly record struct BrokerTradeUpdate(
    BrokerTradeUpdateKind Kind,
    string ClientOrderId,
    string BrokerOrderId,
    decimal FilledQuantity,
    decimal FilledPrice,
    string? Reason,
    long EventUnixNanoseconds);
