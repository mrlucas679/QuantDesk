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
            await CheckOnceAsync(stoppingToken);
            await Task.Delay(ReconciliationInterval, stoppingToken);
        }
    }

    /// <summary>Refresh broker readiness from explicit PAPER and flat-account truth.</summary>
    public async Task CheckOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            bool preserveOperatorMode = IsOperatorOverride(runtimeMode.Snapshot());
            if (!preserveOperatorMode)
                runtimeMode.Transition(SystemMode.Preflight, "paper_broker_preflight");

            BrokerAccountSnapshot? account = await broker.GetAccountAsync(cancellationToken);
            if (!broker.IsPaperEnvironment || account is null || account.TradingBlocked ||
                account.AccountBlocked ||
                !string.Equals(account.Status, "ACTIVE", StringComparison.OrdinalIgnoreCase))
            {
                readiness.RecordBrokerPreflight(false, false, false);
                if (!preserveOperatorMode)
                    runtimeMode.Transition(SystemMode.Degraded, "paper_account_unavailable");
                return;
            }

            if (!preserveOperatorMode)
                runtimeMode.Transition(SystemMode.Syncing, "paper_broker_reconciliation");
            IReadOnlyList<BrokerOrderSnapshot> orders = await broker.ListOpenOrdersAsync(cancellationToken);
            IReadOnlyList<BrokerPositionSnapshot> positions = await broker.ListPositionsAsync(cancellationToken);
            bool flatAndResolved = orders.Count == 0 && positions.All(position => position.Quantity == 0);
            readiness.RecordBrokerPreflight(flatAndResolved, true, broker.IsPaperEnvironment);
            if (!preserveOperatorMode)
            {
                runtimeMode.Transition(
                    readiness.Snapshot().Ready ? SystemMode.Ready : SystemMode.EntryHalted,
                    readiness.Snapshot().Ready ? "full_system_ready" : "full_system_readiness_incomplete");
                logger.LogInformation(
                    "Paper broker preflight completed with {OrderCount} open orders and {PositionCount} positions; reconciled={Reconciled}, full readiness={Ready}.",
                    orders.Count,
                    positions.Count,
                    flatAndResolved,
                    readiness.Snapshot().Ready);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            readiness.RecordBrokerPreflight(false, false, false);
            if (!IsOperatorOverride(runtimeMode.Snapshot()))
                runtimeMode.Transition(SystemMode.Degraded, "paper_broker_unreachable");
            logger.LogWarning(exception, "Paper broker preflight failed.");
        }
    }

    private static bool IsOperatorOverride((SystemMode Mode, string? Reason) snapshot) =>
        (snapshot.Mode is SystemMode.EntryHalted or SystemMode.RiskReductionOnly) &&
        snapshot.Reason is "operator_halt" or "operator_risk_reduction";
}
