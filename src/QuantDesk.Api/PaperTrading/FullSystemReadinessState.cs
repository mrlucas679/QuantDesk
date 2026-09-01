using QuantDesk.Domain.Execution;

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

    /// <summary>
    /// Readiness for *closing* exposure. Everything <see cref="InfrastructureExecutionReady"/> requires
    /// except <see cref="BrokerReconciled"/>.
    ///
    /// That exclusion is the whole point. <c>BrokerReconciled</c> means "the account is flat", so the
    /// instant this system opens a position it stops being reconciled — and gating the exit path on it
    /// made closing a position impossible for exactly as long as one was open. A live BTC/USD diagnostic
    /// sat stranded on this: entry filled, the two-minute hold expired, and every exit attempt was then
    /// refused as INFRASTRUCTURE_NOT_READY because the position it was trying to close existed.
    ///
    /// Requiring flatness is right when *adding* exposure and self-defeating when removing it. The same
    /// asymmetry already governs one layer down, where exit admission deliberately skips the
    /// buying-power check rather than strand a position over a funding shortfall.
    /// </summary>
    public bool ExitExecutionReady => PortfolioKnown && RiskReady && ReservationReady &&
        ExecutionReady && PaperEndpointVerified;

    /// <summary>Research readiness adds candidate and model-plane evidence to infrastructure.</summary>
    public bool StrategyResearchReady => InfrastructureExecutionReady && FeaturesReady && ExpertsReady;

    /// <summary>
    /// Whether this readiness state admits an order of the given classification.
    ///
    /// The single definition of that rule. It previously existed twice — once in
    /// <see cref="ExecutionAdmissionPolicy"/>, which nothing called, and once inline in the diagnostic
    /// lane — and the two disagreed about the exit case. Duplicated admission rules are how the
    /// closing-a-position deadlock survived: fixing one copy left the other wrong.
    ///
    /// <paramref name="closingExposure"/> collapses every classification onto
    /// <see cref="ExitExecutionReady"/>, because the reason to admit a close does not depend on why the
    /// position was opened. Research readiness, market-data health and strategy qualification are
    /// preconditions for taking a position; none of them is a reason to keep one that must be closed.
    /// </summary>
    public bool IsReadyFor(OrderClassification classification, bool closingExposure)
    {
        if (closingExposure) return classification is OrderClassification.DiagnosticExecution
            or OrderClassification.StrategyForwardResearch or OrderClassification.QualifiedStrategy
            && ExitExecutionReady;

        return classification switch
        {
            OrderClassification.DiagnosticExecution => InfrastructureExecutionReady,
            OrderClassification.StrategyForwardResearch => StrategyResearchReady,
            OrderClassification.QualifiedStrategy => Ready,
            _ => false
        };
    }

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
