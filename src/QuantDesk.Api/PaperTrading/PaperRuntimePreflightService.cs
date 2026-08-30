using QuantDesk.Domain.Execution;
using QuantDesk.Domain.Runtime;
using QuantDesk.Runtime.Modes;

namespace QuantDesk.Api.PaperTrading;

public sealed class PaperRuntimePreflightService(
    IBrokerExecutionGateway broker,
    RuntimeModeState runtimeMode,
    FullSystemReadinessState readiness,
    ILogger<PaperRuntimePreflightService> logger) : BackgroundService
{
    private static readonly TimeSpan ReconciliationInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                bool preserveOperatorMode = IsOperatorOverride(runtimeMode.Snapshot());
                if (!preserveOperatorMode)
                    runtimeMode.Transition(SystemMode.Preflight, "paper_broker_preflight");

                BrokerAccountSnapshot? account = await broker.GetAccountAsync(stoppingToken);
                if (account is null || account.TradingBlocked || account.AccountBlocked ||
                    !string.Equals(account.Status, "ACTIVE", StringComparison.OrdinalIgnoreCase))
                {
                    readiness.RecordBrokerPreflight(false, false, false);
                    if (!preserveOperatorMode)
                        runtimeMode.Transition(SystemMode.Degraded, "paper_account_unavailable");
                }
                else
                {
                    if (!preserveOperatorMode)
                        runtimeMode.Transition(SystemMode.Syncing, "paper_broker_reconciliation");
                    IReadOnlyList<BrokerOrderSnapshot> orders = await broker.ListOpenOrdersAsync(stoppingToken);
                    IReadOnlyList<BrokerPositionSnapshot> positions = await broker.ListPositionsAsync(stoppingToken);
                    readiness.RecordBrokerPreflight(true, true, true);
                    if (!preserveOperatorMode)
                    {
                        runtimeMode.Transition(
                            readiness.Snapshot().Ready ? SystemMode.Ready : SystemMode.EntryHalted,
                            readiness.Snapshot().Ready ? "full_system_ready" : "full_system_readiness_incomplete");
                        logger.LogInformation(
                            "Paper broker preflight completed with {OrderCount} open orders and {PositionCount} positions; full readiness is {Ready}.",
                            orders.Count,
                            positions.Count,
                            readiness.Snapshot().Ready);
                    }
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                readiness.RecordBrokerPreflight(false, false, false);
                if (!IsOperatorOverride(runtimeMode.Snapshot()))
                    runtimeMode.Transition(SystemMode.Degraded, "paper_broker_unreachable");
                logger.LogWarning(exception, "Paper broker preflight failed.");
            }

            await Task.Delay(ReconciliationInterval, stoppingToken);
        }
    }

    private static bool IsOperatorOverride((SystemMode Mode, string? Reason) snapshot) =>
        (snapshot.Mode is SystemMode.EntryHalted or SystemMode.RiskReductionOnly) &&
        snapshot.Reason is "operator_halt" or "operator_risk_reduction";
}
