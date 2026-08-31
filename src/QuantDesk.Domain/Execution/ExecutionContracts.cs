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
    string StrategyId)
{
    /// <summary>Optional cash-denominated order size; when set, the broker request omits quantity.</summary>
    public decimal? Notional { get; init; }
}

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

/// <summary>Broker-reported asset eligibility needed before an execution can be admitted.</summary>
public sealed record BrokerAssetSnapshot(
    string Symbol,
    string Status,
    string AssetClass,
    bool Tradable);

/// <summary>Minimal broker account state required to preflight paper execution.</summary>
public sealed record BrokerAccountSnapshot(
    string AccountId,
    string Status,
    decimal Equity,
    decimal BuyingPower,
    bool TradingBlocked,
    bool AccountBlocked)
{
    public string? CryptoTradingStatus { get; init; }
}

public sealed record BrokerOrderSnapshot(
    string BrokerOrderId,
    string ClientOrderId,
    string Status,
    decimal FilledQuantity,
    decimal? AverageFillPrice)
{
    public string? Symbol { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset? SubmittedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public DateTimeOffset? FilledAt { get; init; }
    public DateTimeOffset? CanceledAt { get; init; }
    public DateTimeOffset? ExpiredAt { get; init; }
    public DateTimeOffset? RejectedAt { get; init; }
    /// <summary>Nested broker legs for an atomic multi-leg order, when the broker supplies them.</summary>
    public IReadOnlyList<BrokerOrderLegSnapshot> Legs { get; init; } = [];
}

/// <summary>Broker-truth identity and fill state for one leg of a parent multi-leg order.</summary>
public sealed record BrokerOrderLegSnapshot(
    string BrokerOrderId,
    string Symbol,
    string Status,
    decimal FilledQuantity,
    decimal? AverageFillPrice);

public interface IBrokerExecutionGateway
{
    /// <summary>True only when the adapter has verified an Alpaca paper-trading endpoint.</summary>
    bool IsPaperEnvironment => false;

    Task<BrokerAccountSnapshot?> GetAccountAsync(CancellationToken cancellationToken) =>
        Task.FromResult<BrokerAccountSnapshot?>(null);

    Task<BrokerAssetSnapshot?> GetAssetAsync(string symbol, CancellationToken cancellationToken) =>
        Task.FromResult<BrokerAssetSnapshot?>(null);

    Task<BrokerSubmitResult> SubmitAsync(ExecutionCommand command, CancellationToken cancellationToken);

    Task<BrokerOrderSnapshot?> FindByClientOrderIdAsync(string clientOrderId, CancellationToken cancellationToken);

    Task<IReadOnlyList<BrokerOrderSnapshot>> ListOpenOrdersAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<BrokerOrderSnapshot>>([]);

    Task<IReadOnlyList<BrokerOrderSnapshot>> ListOpenOrdersForSymbolAsync(
        string symbol,
        CancellationToken cancellationToken) => ListOpenOrdersAsync(cancellationToken);

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
