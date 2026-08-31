namespace QuantDesk.Api.PaperTrading;

public sealed record FullSystemReadinessSnapshot(
    bool MarketDataHealthy,
    bool TradeUpdatesHealthy,
    bool BrokerReconciled,
    bool PortfolioKnown,
    bool FeaturesReady,
    bool ExpertsReady,
    bool CommitteesReady,
    bool RiskReady,
    bool ReservationReady,
    bool ExecutionReady,
    bool ExitEngineReady,
    bool PaperEndpointVerified,
    DateTimeOffset UpdatedAt)
{
    /// <summary>
    /// Infrastructure-only readiness used by diagnostic paper orders. Research, strategy,
    /// market-signal, and exit-lifecycle readiness are intentionally outside this admission.
    /// </summary>
    public bool InfrastructureExecutionReady => BrokerReconciled && PortfolioKnown && RiskReady &&
        ReservationReady && ExecutionReady && PaperEndpointVerified;

    /// <summary>Research readiness adds candidate and model-plane evidence to infrastructure.</summary>
    public bool StrategyResearchReady => InfrastructureExecutionReady && FeaturesReady && ExpertsReady;

    public bool Ready => MarketDataHealthy && TradeUpdatesHealthy && BrokerReconciled &&
        PortfolioKnown && FeaturesReady && ExpertsReady && CommitteesReady && RiskReady &&
        ReservationReady && ExecutionReady && ExitEngineReady && PaperEndpointVerified;
}

/// <summary>Records independently verified readiness gates for autonomous paper execution.</summary>
public sealed class FullSystemReadinessState
{
    private readonly object _gate = new();
    private FullSystemReadinessSnapshot _snapshot = EmptySnapshot();

    public FullSystemReadinessSnapshot Snapshot()
    {
        lock (_gate) return _snapshot;
    }

    public void RecordBrokerPreflight(bool reconciled, bool portfolioKnown, bool paperEndpointVerified)
    {
        lock (_gate)
        {
            _snapshot = _snapshot with
            {
                BrokerReconciled = reconciled,
                PortfolioKnown = portfolioKnown,
                PaperEndpointVerified = paperEndpointVerified,
                UpdatedAt = DateTimeOffset.UtcNow
            };
        }
    }

    public void RecordDeterministicRuntime(
        bool committeesReady,
        bool riskReady,
        bool reservationReady,
        bool executionReady,
        bool exitEngineReady)
    {
        lock (_gate)
        {
            _snapshot = _snapshot with
            {
                CommitteesReady = committeesReady,
                RiskReady = riskReady,
                ReservationReady = reservationReady,
                ExecutionReady = executionReady,
                ExitEngineReady = exitEngineReady,
                UpdatedAt = DateTimeOffset.UtcNow
            };
        }
    }

    public void RecordResearchPlane(bool featuresReady, bool expertsReady)
    {
        lock (_gate)
        {
            _snapshot = _snapshot with
            {
                FeaturesReady = featuresReady,
                ExpertsReady = expertsReady,
                UpdatedAt = DateTimeOffset.UtcNow
            };
        }
    }

    public void RecordStreams(bool marketDataHealthy, bool tradeUpdatesHealthy)
    {
        lock (_gate)
        {
            _snapshot = _snapshot with
            {
                MarketDataHealthy = marketDataHealthy,
                TradeUpdatesHealthy = tradeUpdatesHealthy,
                UpdatedAt = DateTimeOffset.UtcNow
            };
        }
    }

    private static FullSystemReadinessSnapshot EmptySnapshot() => new(
        false, false, false, false, false, false, false, false, false, false, false, false,
        DateTimeOffset.UtcNow);
}
