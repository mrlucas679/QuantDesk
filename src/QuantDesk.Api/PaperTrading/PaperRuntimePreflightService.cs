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
                // The mode reflects what *this* service knows: whether the broker and runtime are in a
                // state to act. It deliberately does not include research readiness.
                //
                // It used to key on full readiness, which includes featuresReady and expertsReady. No
                // strategy qualifies, so full readiness is unreachable, so the runtime sat permanently
                // in EntryHalted — and EntryHalted blocks manual operator orders. A dark research plane
                // was silently disabling the human's controls, which is not what "entry halted" is for.
                //
                // Strategy qualification is still enforced where strategy orders are admitted: the
                // autonomous lane requires a ready research plane and a forecast, ExecutionAdmissionPolicy
                // maps QualifiedStrategy onto full readiness, and ExecutionWorker requires an active risk
                // reservation before any of it.
                bool runtimeReady = readiness.Snapshot().InfrastructureExecutionReady;
                runtimeMode.Transition(
                    runtimeReady ? SystemMode.Ready : SystemMode.EntryHalted,
                    runtimeReady ? "broker_preflight_reconciled" : "broker_preflight_incomplete");
                logger.LogInformation(
                    "Paper broker preflight completed with {OrderCount} open orders and {PositionCount} positions; reconciled={Reconciled}, full readiness={Ready}.",
                    orders.Count,
                    positions.Count,
                    flatAndResolved,
                    readiness.Snapshot().Ready);
            }
        }
        catch (Exception exception) when (HostedServiceFaults.IsFault(exception, cancellationToken))
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
