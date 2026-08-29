using System.Diagnostics;
using QuantDesk.Alpaca.Mapping;
using QuantDesk.Alpaca.MarketData;
using QuantDesk.Domain.Execution;
using QuantDesk.Domain.Runtime;
using QuantDesk.Domain.Trading;
using QuantDesk.Runtime.Modes;

namespace QuantDesk.Api.PaperTrading;

public sealed class AutonomousPaperTradingService(
    IBrokerExecutionGateway broker,
    IInstrumentSymbolResolver symbols,
    AlpacaLatestCryptoQuoteClient quoteClient,
    AutonomousPaperTradingOptions options,
    RuntimeModeState runtimeMode,
    AutonomousTradingState state,
    ILogger<AutonomousPaperTradingService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            state.Update("disabled", options.Symbol);
            return;
        }

        try
        {
            await WaitUntilReadyAsync(stoppingToken);
            await ExecuteRoundTripAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            state.Update("stopped", options.Symbol);
        }
        catch (Exception exception)
        {
            state.Update("failed", options.Symbol, reason: "AUTONOMOUS_CYCLE_FAILED");
            runtimeMode.Transition(SystemMode.Degraded, "autonomous_paper_cycle_failed");
            logger.LogError(exception, "Autonomous paper-trading cycle failed.");
        }
    }

    private async Task ExecuteRoundTripAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<BrokerOrderSnapshot> openOrders = await broker.ListOpenOrdersAsync(cancellationToken);
        IReadOnlyList<BrokerPositionSnapshot> positions = await broker.ListPositionsAsync(cancellationToken);
        if (openOrders.Count != 0 || positions.Any(position =>
                string.Equals(position.Symbol, options.Symbol, StringComparison.OrdinalIgnoreCase) && position.Quantity != 0))
            throw new InvalidOperationException("Autonomous canary requires no open orders or existing position in its symbol.");

        if (!symbols.TryResolveBySymbol(options.Symbol, out int slot))
            throw new InvalidOperationException("Autonomous symbol is not mapped to an instrument slot.");
        decimal ask = await quoteClient.GetAskAsync(options.Symbol, cancellationToken);
        decimal quantity = decimal.Round(options.OrderNotional / ask, 8, MidpointRounding.ToZero);
        if (quantity <= 0) throw new InvalidOperationException("Calculated autonomous order quantity is zero.");

        string entryClientId = $"qd-auto-entry-{Guid.NewGuid():N}";
        state.Update("submitting_entry", options.Symbol, filledQuantity: quantity);
        BrokerSubmitResult entry = await SubmitMarketAsync(
            slot, OrderSide.Buy, PositionIntent.Open, quantity, entryClientId, ExecutionPriority.ExplorationEntry,
            cancellationToken);
        EnsureAcknowledged(entry, "entry");

        BrokerOrderSnapshot entryFill = await WaitForFillAsync(entryClientId, entry.BrokerOrderId!, cancellationToken);
        decimal filledQuantity = entryFill.FilledQuantity;
        state.Update("entry_filled", options.Symbol, entry.BrokerOrderId, filledQuantity: filledQuantity);

        try
        {
            await Task.Delay(options.HoldDuration, cancellationToken);
        }
        finally
        {
            await ClosePositionAsync(slot, entry.BrokerOrderId!, cancellationToken);
        }
    }

    private async Task ClosePositionAsync(
        int slot, string entryOrderId, CancellationToken cancellationToken)
    {
        decimal quantity = await ReadPositionQuantityAsync(slot, cancellationToken);
        string exitClientId = $"qd-auto-exit-{Guid.NewGuid():N}";
        state.Update("submitting_exit", options.Symbol, entryOrderId, filledQuantity: quantity);
        BrokerSubmitResult exit = await SubmitMarketAsync(
            slot, OrderSide.Sell, PositionIntent.Close, quantity, exitClientId, ExecutionPriority.EmergencyExit,
            cancellationToken);
        string? exitOrderId = exit.BrokerOrderId;
        if (exit.State == BrokerSubmitState.Acknowledged && !string.IsNullOrWhiteSpace(exitOrderId))
        {
            await WaitForFillAsync(exitClientId, exitOrderId, cancellationToken);
        }
        else
        {
            BrokerSubmitResult liquidation = await broker.ClosePositionAsync(slot, cancellationToken);
            EnsureAcknowledged(liquidation, "liquidation");
            exitOrderId = liquidation.BrokerOrderId;
        }
        await WaitUntilFlatAsync(slot, cancellationToken);
        state.Update("completed_flat", options.Symbol, entryOrderId, exitOrderId, quantity);
        logger.LogInformation(
            "Autonomous paper round trip completed for {Symbol}; entry {EntryOrderId}, exit {ExitOrderId}.",
            options.Symbol, entryOrderId, exitOrderId);
    }

    private async Task<BrokerSubmitResult> SubmitMarketAsync(
        int slot,
        OrderSide side,
        PositionIntent intent,
        decimal quantity,
        string clientOrderId,
        ExecutionPriority priority,
        CancellationToken cancellationToken)
    {
        BrokerAccountSnapshot? account = await broker.GetAccountAsync(cancellationToken);
        if (account is null || account.TradingBlocked || account.AccountBlocked ||
            !string.Equals(account.Status, "ACTIVE", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Paper account is not available for autonomous execution.");
        if (side == OrderSide.Buy && options.OrderNotional > account.BuyingPower)
            throw new InvalidOperationException("Paper account buying power is below the autonomous order notional.");

        long now = Stopwatch.GetTimestamp();
        var command = new ExecutionCommand(
            now, priority, 0, 0, clientOrderId, slot, side, intent, ExecutionOrderType.Market,
            ExecutionTimeInForce.Gtc, quantity, null, now, now + Stopwatch.Frequency * 30,
            "autonomous-paper-canary");
        return await broker.SubmitAsync(command, cancellationToken);
    }

    private async Task<BrokerOrderSnapshot> WaitForFillAsync(
        string clientOrderId, string brokerOrderId, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.FillTimeout);
        try
        {
            while (true)
            {
                BrokerOrderSnapshot? order = await broker.FindByClientOrderIdAsync(clientOrderId, timeout.Token);
                if (order is not null && string.Equals(order.Status, "filled", StringComparison.OrdinalIgnoreCase) &&
                    order.FilledQuantity > 0)
                    return order;
                if (order is not null && IsTerminalFailure(order.Status))
                    throw new InvalidOperationException("Autonomous paper order reached a non-fill terminal state.");
                await Task.Delay(TimeSpan.FromMilliseconds(250), timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await broker.CancelAsync(brokerOrderId, cancellationToken);
            throw new TimeoutException("Autonomous paper order did not fill before its deadline.");
        }
    }

    private async Task WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        state.Update("waiting_for_runtime", options.Symbol);
        while (runtimeMode.Snapshot().Mode != SystemMode.Ready)
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
    }

    private async Task<decimal> ReadPositionQuantityAsync(int slot, CancellationToken cancellationToken)
    {
        IReadOnlyList<BrokerPositionSnapshot> positions = await broker.ListPositionsAsync(cancellationToken);
        BrokerPositionSnapshot? position = positions.FirstOrDefault(candidate => candidate.InstrumentSlot == slot);
        if (position is null || position.Quantity <= 0)
            throw new InvalidOperationException("Broker reconciliation did not find the autonomous position.");
        return position.Quantity;
    }

    private async Task WaitUntilFlatAsync(int slot, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.FillTimeout);
        while (true)
        {
            IReadOnlyList<BrokerPositionSnapshot> positions = await broker.ListPositionsAsync(timeout.Token);
            if (positions.All(position => position.InstrumentSlot != slot || position.Quantity == 0)) return;
            await Task.Delay(TimeSpan.FromMilliseconds(250), timeout.Token);
        }
    }

    private static bool IsTerminalFailure(string status) => status is
        "canceled" or "expired" or "rejected" or "suspended";

    private static void EnsureAcknowledged(BrokerSubmitResult result, string stage)
    {
        if (result.State != BrokerSubmitState.Acknowledged || string.IsNullOrWhiteSpace(result.BrokerOrderId))
            throw new InvalidOperationException($"Autonomous {stage} order was not acknowledged by the paper broker.");
    }
}
