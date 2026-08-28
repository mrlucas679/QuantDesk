using QuantDesk.Domain.Execution;
using QuantDesk.Domain.Runtime;
using QuantDesk.Runtime.Modes;

namespace QuantDesk.Api.PaperTrading;

public sealed class PaperRuntimePreflightService(
    IBrokerExecutionGateway broker,
    RuntimeModeState runtimeMode,
    ILogger<PaperRuntimePreflightService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            runtimeMode.Transition(SystemMode.Preflight, "paper_broker_preflight");
            try
            {
                BrokerAccountSnapshot? account = await broker.GetAccountAsync(stoppingToken);
                if (account is null || account.TradingBlocked || account.AccountBlocked ||
                    !string.Equals(account.Status, "ACTIVE", StringComparison.OrdinalIgnoreCase))
                {
                    runtimeMode.Transition(SystemMode.Degraded, "paper_account_unavailable");
                }
                else
                {
                    runtimeMode.Transition(SystemMode.Syncing, "paper_broker_reconciliation");
                    IReadOnlyList<BrokerOrderSnapshot> orders = await broker.ListOpenOrdersAsync(stoppingToken);
                    IReadOnlyList<BrokerPositionSnapshot> positions = await broker.ListPositionsAsync(stoppingToken);
                    if (runtimeMode.Snapshot().Mode == SystemMode.Syncing)
                    {
                        runtimeMode.Transition(SystemMode.Ready, "paper_broker_reconciled");
                        logger.LogInformation(
                            "Paper broker preflight completed with {OrderCount} open orders and {PositionCount} positions.",
                            orders.Count,
                            positions.Count);
                    }
                    return;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                runtimeMode.Transition(SystemMode.Degraded, "paper_broker_unreachable");
                logger.LogWarning(exception, "Paper broker preflight failed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }
}
