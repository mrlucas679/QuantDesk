using QuantDesk.Domain.Trading;

namespace QuantDesk.Domain.Execution;

public enum ExecutionPriority
{
    EmergencyExit = 0,
    RiskReduction = 1,
    NormalExit = 2,
    ExploitationEntry = 3,
    ExplorationEntry = 4
}

public enum ExecutionIntentState
{
    Created,
    Approved,
    Reserved,
    Queued,
    Submitted,
    Acknowledged,
    PartiallyFilled,
    Filled,
    PositionManaging,
    Closing,
    Reconciling,
    Completed,
    Failed,
    Canceled
}

public enum OrderOwnership
{
    Local,
    ExternalVenue,
    Reconciliation
}

public enum BrokerSubmitState
{
    Acknowledged,
    Rejected,
    Unknown
}

public sealed record ExecutionCommand(
    long CommandId,
    ExecutionPriority Priority,
    long RiskReservationId,
    long CapitalReservationId,
    string ClientOrderId,
    int InstrumentSlot,
    OrderSide Side,
    PositionIntent PositionIntent,
    ExecutionOrderType OrderType,
    ExecutionTimeInForce TimeInForce,
    decimal Quantity,
    decimal? LimitPrice,
    long CreatedMonotonicTicks,
    long ExpiresMonotonicTicks,
    string StrategyId);

public sealed record BrokerSubmitResult(
    BrokerSubmitState State,
    string? BrokerOrderId,
    string? ReasonCode,
    string? RequestId);

public sealed record BrokerPositionSnapshot(
    string Symbol,
    int InstrumentSlot,
    decimal Quantity,
    decimal AveragePrice);

/// <summary>Minimal broker account state required to preflight paper execution.</summary>
public sealed record BrokerAccountSnapshot(
    string AccountId,
    string Status,
    decimal Equity,
    decimal BuyingPower,
    bool TradingBlocked,
    bool AccountBlocked);

public sealed record BrokerOrderSnapshot(
    string BrokerOrderId,
    string ClientOrderId,
    string Status,
    decimal FilledQuantity,
    decimal? AverageFillPrice);

public interface IBrokerExecutionGateway
{
    Task<BrokerAccountSnapshot?> GetAccountAsync(CancellationToken cancellationToken) =>
        Task.FromResult<BrokerAccountSnapshot?>(null);

    Task<BrokerSubmitResult> SubmitAsync(ExecutionCommand command, CancellationToken cancellationToken);

    Task<BrokerOrderSnapshot?> FindByClientOrderIdAsync(string clientOrderId, CancellationToken cancellationToken);

    Task<IReadOnlyList<BrokerOrderSnapshot>> ListOpenOrdersAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<BrokerOrderSnapshot>>([]);

    Task<IReadOnlyList<BrokerPositionSnapshot>> ListPositionsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<BrokerPositionSnapshot>>([]);

    Task<BrokerSubmitResult> CancelAsync(string brokerOrderId, CancellationToken cancellationToken) =>
        Task.FromResult(new BrokerSubmitResult(BrokerSubmitState.Rejected, brokerOrderId, "CANCEL_NOT_SUPPORTED", null));

    Task<BrokerSubmitResult> ReplaceAsync(
        string brokerOrderId,
        decimal? quantity,
        decimal? limitPrice,
        CancellationToken cancellationToken) =>
        Task.FromResult(new BrokerSubmitResult(BrokerSubmitState.Rejected, brokerOrderId, "REPLACE_NOT_SUPPORTED", null));

    Task<BrokerSubmitResult> ClosePositionAsync(int instrumentSlot, CancellationToken cancellationToken) =>
        Task.FromResult(new BrokerSubmitResult(BrokerSubmitState.Rejected, null, "CLOSE_POSITION_NOT_SUPPORTED", null));
}
