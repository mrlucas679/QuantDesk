using System.Diagnostics;
using QuantDesk.Alpaca.Mapping;
using QuantDesk.Domain.Execution;
using QuantDesk.Domain.Runtime;
using QuantDesk.Domain.Trading;
using QuantDesk.Runtime.Modes;

namespace QuantDesk.Api.PaperTrading;

public sealed record PaperOrderRequest(
    string Symbol,
    string Side,
    decimal Quantity,
    decimal LimitPrice,
    string? ClientOrderId);

public sealed record PaperOrderSubmission(
    bool Accepted,
    string ClientOrderId,
    string? BrokerOrderId,
    string? ReasonCode,
    string? BrokerRequestId);

public sealed class PaperOrderApplicationService(
    IBrokerExecutionGateway broker,
    IInstrumentSymbolResolver symbols,
    PaperTradingOptions options,
    RuntimeModeState runtimeMode)
{
    public async Task<PaperOrderSubmission> SubmitAsync(PaperOrderRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        string clientOrderId = NormalizeClientOrderId(request.ClientOrderId);
        if (clientOrderId.Length == 0) return Reject(string.Empty, "INVALID_CLIENT_ORDER_ID");
        if (runtimeMode.Snapshot().Mode != SystemMode.Ready)
            return Reject(clientOrderId, "RUNTIME_NOT_READY");
        if (!TryValidate(request, out int slot, out OrderSide side, out string? reason))
            return Reject(clientOrderId, reason!);

        BrokerAccountSnapshot? account = await broker.GetAccountAsync(cancellationToken);
        if (account is null || account.TradingBlocked || account.AccountBlocked ||
            !string.Equals(account.Status, "ACTIVE", StringComparison.OrdinalIgnoreCase))
            return Reject(clientOrderId, "PAPER_ACCOUNT_UNAVAILABLE");

        if (request.Quantity > options.MaximumOrderNotional / request.LimitPrice)
            return Reject(clientOrderId, "ORDER_NOTIONAL_LIMIT");
        decimal notional = request.Quantity * request.LimitPrice;
        if (notional > account.BuyingPower) return Reject(clientOrderId, "BUYING_POWER_LIMIT");

        long now = Stopwatch.GetTimestamp();
        var command = new ExecutionCommand(
            now,
            ExecutionPriority.ExplorationEntry,
            0,
            0,
            clientOrderId,
            slot,
            side,
            PositionIntent.Open,
            ExecutionOrderType.Limit,
            ExecutionTimeInForce.Day,
            request.Quantity,
            request.LimitPrice,
            now,
            now + (Stopwatch.Frequency * 30),
            "operator-paper-order");
        BrokerSubmitResult result = await broker.SubmitAsync(command, cancellationToken);
        return new PaperOrderSubmission(
            result.State == BrokerSubmitState.Acknowledged,
            clientOrderId,
            result.BrokerOrderId,
            result.ReasonCode,
            result.RequestId);
    }

    public Task<IReadOnlyList<BrokerOrderSnapshot>> ListOpenAsync(CancellationToken cancellationToken) =>
        broker.ListOpenOrdersAsync(cancellationToken);

    public Task<BrokerSubmitResult> CancelAsync(string brokerOrderId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brokerOrderId);
        return broker.CancelAsync(brokerOrderId, cancellationToken);
    }

    private bool TryValidate(PaperOrderRequest request, out int slot, out OrderSide side, out string? reason)
    {
        slot = -1;
        side = default;
        reason = null;
        if (string.IsNullOrWhiteSpace(request.Symbol) ||
            !symbols.TryResolveBySymbol(request.Symbol.Trim(), out slot)) reason = "SYMBOL_NOT_ALLOWED";
        else if (string.Equals(request.Side, "buy", StringComparison.OrdinalIgnoreCase)) side = OrderSide.Buy;
        else if (string.Equals(request.Side, "sell", StringComparison.OrdinalIgnoreCase)) side = OrderSide.Sell;
        else reason = "INVALID_SIDE";
        if (reason is null && (request.Quantity <= 0 || request.LimitPrice <= 0)) reason = "INVALID_ORDER_VALUE";
        return reason is null;
    }

    private static string NormalizeClientOrderId(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested)) return $"qd-api-{Guid.NewGuid():N}";
        string value = requested.Trim();
        if (value.Length > 48 || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            return string.Empty;
        return value;
    }

    private static PaperOrderSubmission Reject(string clientOrderId, string reason) =>
        new(false, clientOrderId, null, reason, null);
}
