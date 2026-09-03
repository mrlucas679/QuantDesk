using System.Diagnostics;
using QuantDesk.Alpaca.Mapping;
using QuantDesk.Domain.Execution;
using QuantDesk.Domain.Runtime;
using QuantDesk.Domain.Trading;
using QuantDesk.Runtime.Modes;
using QuantDesk.Runtime.Time;

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
    RuntimeModeState runtimeMode,
    FullSystemReadinessState readiness,
    IRuntimeClock clock)
{
    public async Task<PaperOrderSubmission> SubmitAsync(PaperOrderRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        string clientOrderId = NormalizeClientOrderId(request.ClientOrderId);
        if (clientOrderId.Length == 0) return Reject(string.Empty, "INVALID_CLIENT_ORDER_ID");
        if (!TryValidate(request, out int slot, out OrderSide side, out string? reason))
            return Reject(clientOrderId, reason!);

        // A manual operator order is bounded, key-authorised and notional-capped. It is admitted on
        // *infrastructure* readiness — the same bar the diagnostic lane clears to place a real order —
        // not on full system readiness.
        //
        // Requiring SystemMode.Ready meant requiring featuresReady and expertsReady, which describe the
        // research plane and say nothing about whether a hand-placed order is safe. Since no strategy
        // qualifies, Ready is unreachable, so the operator's manual path could never accept an order at
        // all: an escape hatch permanently welded shut by the state of an unrelated subsystem.
        SystemMode mode = runtimeMode.Snapshot().Mode;
        if (mode is SystemMode.Emergency or SystemMode.Shutdown or SystemMode.Booting or SystemMode.Degraded)
            return Reject(clientOrderId, "RUNTIME_NOT_READY");

        bool reducesExposure = await ReducesExposureAsync(slot, side, request.Quantity, cancellationToken);
        if (!readiness.Snapshot().IsReadyFor(OrderClassification.DiagnosticExecution, reducesExposure))
            return Reject(clientOrderId, "INFRASTRUCTURE_NOT_READY");

        // EntryHalted and RiskReductionOnly mean "stop adding exposure", not "stop trading".
        bool riskReductionOnly = mode is SystemMode.EntryHalted or SystemMode.RiskReductionOnly;
        if (riskReductionOnly && !reducesExposure) return Reject(clientOrderId, "ENTRY_HALTED");

        BrokerAccountSnapshot? account = await broker.GetAccountAsync(cancellationToken);
        if (account is null || account.TradingBlocked || account.AccountBlocked ||
            !string.Equals(account.Status, "ACTIVE", StringComparison.OrdinalIgnoreCase))
            return Reject(clientOrderId, "PAPER_ACCOUNT_UNAVAILABLE");

        if (request.Quantity > options.MaximumOrderNotional / request.LimitPrice)
            return Reject(clientOrderId, "ORDER_NOTIONAL_LIMIT");
        decimal notional = request.Quantity * request.LimitPrice;
        if (notional > account.BuyingPower) return Reject(clientOrderId, "BUYING_POWER_LIMIT");

        // Through the clock, so an operator order placed under a replayed or virtual clock
        // carries a deadline in the same units its own timestamp is in.
        long now = clock.MonotonicTimestamp;
        var command = new ExecutionCommand(
            now,
            ExecutionPriority.ExplorationEntry,
            0,
            0,
            clientOrderId,
            slot,
            side,
            reducesExposure ? PositionIntent.Close : PositionIntent.Open,
            ExecutionOrderType.Limit,
            ExecutionTimeInForce.Day,
            request.Quantity,
            request.LimitPrice,
            now,
            now + clock.MonotonicTicksFor(TimeSpan.FromSeconds(30)),
            "operator-paper-order");
        BrokerSubmitResult result = await broker.SubmitAsync(command, cancellationToken);
        return new PaperOrderSubmission(
            result.State == BrokerSubmitState.Acknowledged,
            clientOrderId,
            result.BrokerOrderId,
            result.ReasonCode,
            result.RequestId);
    }

    /// <summary>
    /// True when the order moves the position toward flat without crossing through it.
    ///
    /// An order larger than the position is not risk reduction: closing 2 of a 1-lot long opens a short.
    /// Treating it as a close would let an operator open new exposure through the one path that stays
    /// available while entry is halted.
    /// </summary>
    private async Task<bool> ReducesExposureAsync(
        int slot, OrderSide side, decimal quantity, CancellationToken cancellationToken)
    {
        IReadOnlyList<BrokerPositionSnapshot> positions = await broker.ListPositionsAsync(cancellationToken);
        decimal held = positions
            .Where(position => position.InstrumentSlot == slot)
            .Sum(position => position.Quantity);

        if (held > 0) return side == OrderSide.Sell && quantity <= held;
        if (held < 0) return side == OrderSide.Buy && quantity <= -held;
        return false;
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
