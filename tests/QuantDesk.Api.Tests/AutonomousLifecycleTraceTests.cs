using System.Text;
using QuantDesk.Alpaca.MarketData;
using QuantDesk.Api.PaperTrading;
using QuantDesk.Domain.Execution;
using QuantDesk.Domain.Numerics;
using QuantDesk.Domain.Portfolio;
using QuantDesk.Domain.Risk;
using QuantDesk.Domain.Strategies;
using QuantDesk.Domain.Trading;
using QuantDesk.Runtime.Actionability;
using QuantDesk.Runtime.Costs;
using QuantDesk.Runtime.Execution;
using QuantDesk.Runtime.Experts;
using QuantDesk.Domain.Runtime;
using QuantDesk.Runtime.Modes;
using QuantDesk.Runtime.Reservations;
using QuantDesk.Runtime.Risk;
using QuantDesk.Runtime.State;
using QuantDesk.Runtime.Strategies;
using QuantDesk.Runtime.Time;
using Xunit.Abstractions;

namespace QuantDesk.Api.Tests;

/// <summary>
/// Traces one autonomous opportunity through every stage the application owns, from market
/// evidence to the exact order the Alpaca adapter would POST, and records the verdict of each
/// stage. Its purpose is diagnostic: when no trade is reaching the broker, this test names the
/// stage that stopped it instead of leaving the answer to inspection.
///
/// The broker is a recording double, so nothing here contacts Alpaca and no order is placed.
/// </summary>
public sealed class AutonomousLifecycleTraceTests(ITestOutputHelper output)
{
    private const int Slot = 0;
    private static readonly Usd OrderNotional = new(20m);

    [Fact]
    public void StrongMomentumReachesTheBrokerSubmissionBoundary()
    {
        LifecycleTrace trace = RunLifecycle(Evidence(100m, 100.01m, 100m, 104m));

        output.WriteLine(trace.Render());
        Assert.True(trace.ReachedBroker, $"Lifecycle stopped at: {trace.StoppedAt}");
        Assert.Equal(BrokerSubmitState.Acknowledged, trace.SubmitState);
        Assert.NotNull(trace.SubmittedCommand);
        Assert.Equal(OrderSide.Buy, trace.SubmittedCommand!.Side);
        Assert.True(trace.SubmittedCommand.Quantity > 0);
        // Reservation must be committed before the order reaches the broker.
        Assert.True(trace.ReservationId > 0);
    }

    [Fact]
    public void RealisticSmallMomentumStopsAtTheCostGateAndNamesIt()
    {
        LifecycleTrace trace = RunLifecycle(Evidence(100m, 100.01m, 100m, 100.3m));

        output.WriteLine(trace.Render());
        Assert.False(trace.ReachedBroker);
        Assert.Equal("EXPECTED_EDGE_BELOW_COSTS", trace.StoppedAt);
    }

    [Fact]
    public void UnreconciledBrokerStateStopsBeforeAnyOrder()
    {
        LifecycleTrace trace = RunLifecycle(
            Evidence(100m, 100.01m, 100m, 104m), portfolioReconciled: false);

        output.WriteLine(trace.Render());
        Assert.False(trace.ReachedBroker);
        Assert.Equal(RiskReason.PortfolioUnreconciled.ToString(), trace.StoppedAt);
    }

    [Fact]
    public void VenueCostNotSignalLogicDecidesWhetherAnyTradeIsAdmissible()
    {
        // The same momentum logic, the same pipeline, the same risk limits — only the venue cost
        // profile differs. Spot crypto pays 50 bps of round-trip fees; US equities pay none. This
        // records the move each venue demands before the application will place an order, which
        // is the single reason no trade has ever been admitted.
        decimal[] moves = [0.10m, 0.25m, 0.50m, 0.75m, 1.00m, 2.00m, 4.00m];
        var table = new StringBuilder();
        table.AppendLine("13-bar move -> verdict, by venue cost profile");
        table.AppendLine($"  {"move",6}  {"spot-crypto (50bps fees)",-28}  us-equity (commission-free)");
        foreach (decimal movePercent in moves)
        {
            DirectionalMarketEvidence evidence = Evidence(
                100m, 100.01m, 100m, 100m * (1m + movePercent / 100m));
            LifecycleTrace crypto = RunLifecycle(evidence);
            LifecycleTrace equity = RunLifecycle(evidence, costs: ExecutionCostProfile.UsEquity);
            table.AppendLine(
                $"  {movePercent,5:0.00}%  {Verdict(crypto),-28}  {Verdict(equity)}");
        }

        output.WriteLine(table.ToString());

        // Spot crypto needs a move between 2% and 4% over roughly an hour before it will trade.
        Assert.False(RunLifecycle(Evidence(100m, 100.01m, 100m, 102m)).ReachedBroker);
        // The identical signal on a commission-free venue is admissible an order of magnitude
        // earlier, which is what makes an autonomous trade reachable at all.
        Assert.True(RunLifecycle(
            Evidence(100m, 100.01m, 100m, 100.5m), costs: ExecutionCostProfile.UsEquity).ReachedBroker);
    }

    private static string Verdict(LifecycleTrace trace) =>
        trace.ReachedBroker ? "SUBMITTED" : trace.StoppedAt;

    private static LifecycleTrace RunLifecycle(
        DirectionalMarketEvidence evidence,
        bool brokerHealthy = true,
        bool portfolioReconciled = true,
        ExecutionCostProfile? costs = null)
    {
        var trace = new LifecycleTrace();
        var clock = new VirtualRuntimeClock(DateTimeOffset.Parse("2026-08-31T12:00:00Z"));
        PortfolioSnapshot portfolio = Portfolio();
        trace.Add("market-evidence", $"bid={evidence.Bid} ask={evidence.Ask} bars={evidence.Closes.Count}");

        AutonomousPipelineDecision decision = CreatePipeline(clock, costs)
            .Evaluate(Slot, evidence, portfolio, brokerHealthy, portfolioReconciled);
        trace.Add("committee", decision.Committee is { } c
            ? $"actionable={c.Actionable} expectedBps={c.ExpectedReturnBps:0.00} experts={c.SupportingExperts.Count}"
            : "not reached");
        trace.Add("strategy-compiler", decision.Candidate is { } cand
            ? $"candidate={cand.StrategyId} exit={cand.ManagementPlan.ExitPolicyVersion}"
            : "no candidate");
        trace.Add("cost-model", decision.Costs is { } cost
            ? $"total={cost.Total.Value:0.0000} usd" : "not reached");
        trace.Add("risk-governor", decision.Risk is { } risk
            ? $"approved={risk.Approved} reason={risk.Reason}" : "not reached");

        if (!decision.Approved || decision.Candidate is not TradeCandidate candidate ||
            decision.Risk is not { Approved: true } approved)
        {
            trace.StoppedAt = decision.Reason;
            return trace;
        }

        var reservations = new ReservationLedger(portfolio);
        if (!reservations.TryReserve(portfolio.Version, approved.RequiredRiskReservation,
                approved.RequiredCapitalReservation, OrderNotional, out PortfolioReservation? reservation) ||
            reservation is null)
        {
            trace.Add("reservation", "REJECTED");
            trace.StoppedAt = "ReservationRejected";
            return trace;
        }

        trace.ReservationId = reservation.ReservationId;
        trace.Add("reservation", $"committed id={reservation.ReservationId} (before any broker call)");

        decimal quantity = decimal.Round(OrderNotional.Value / evidence.Ask, 8, MidpointRounding.ToZero);
        if (quantity <= 0)
        {
            trace.StoppedAt = "QuantityRoundedToZero";
            return trace;
        }

        var broker = new RecordingBroker();
        var runtimeMode = new RuntimeModeState();
        runtimeMode.Transition(SystemMode.Ready, "trace");
        var worker = new ExecutionWorker(broker, reservations, runtimeMode, TimeSpan.FromSeconds(30));
        string clientOrderId = "qd-trace-entry-0001";
        var command = new ExecutionCommand(
            1, ExecutionPriority.ExploitationEntry, reservation.ReservationId, reservation.ReservationId,
            clientOrderId, Slot, OrderSide.Buy, PositionIntent.Open, ExecutionOrderType.Market,
            ExecutionTimeInForce.Ioc, quantity, null,
            clock.MonotonicTimestamp, clock.MonotonicTimestamp + 1_000_000_000, candidate.StrategyId);
        var intent = new ExecutionIntent(
            candidate.CandidateId, candidate.CandidateId, candidate.StrategyId);
        intent.TransitionTo(ExecutionIntentState.Approved);
        intent.AttachApproval(clientOrderId, reservation.ReservationId, reservation.ReservationId);
        intent.TransitionTo(ExecutionIntentState.Queued);

        BrokerSubmitResult result = worker
            .SubmitOneAsync(intent, command, clock.MonotonicTimestamp, CancellationToken.None)
            .GetAwaiter().GetResult();

        trace.SubmittedCommand = broker.LastCommand;
        trace.SubmitState = result.State;
        trace.ReachedBroker = broker.LastCommand is not null;
        trace.Add("broker-submission",
            $"state={result.State} brokerOrderId={result.BrokerOrderId} clientOrderId={clientOrderId} qty={quantity}");
        if (!trace.ReachedBroker) trace.StoppedAt = "ExecutionWorkerDidNotSubmit";
        return trace;
    }

    private static AutonomousDecisionPipeline CreatePipeline(
        IRuntimeClock clock, ExecutionCostProfile? costs = null)
    {
        ExecutionCostProfile profile = costs ?? ExecutionCostProfile.SpotCryptoTaker;
        return new AutonomousDecisionPipeline(
            new MarketStateStore(1),
            new ExpertCommittee(0.6, 1),
            new CryptoDirectionalStrategyCompiler(OrderNotional, 0.05,
                TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)),
            new CryptoResearchGate(profile),
            new CryptoCostModel(
                new BasisPoints((double)profile.RoundTripFeeBps),
                new BasisPoints((double)profile.SlippageAllowanceBps)),
            new ActionabilityGate(0.01, new Usd(0.01m)),
            new RiskGovernor(new RiskLimits(new Usd(5), new Usd(25), new Usd(100),
                new Usd(250), 1, 100_000, 100_000, 100_000, 0.01, 1)),
            clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AutonomousDecisionPipeline>.Instance);
    }

    private static DirectionalMarketEvidence Evidence(decimal bid, decimal ask, decimal first, decimal last)
    {
        decimal step = (last - first) / 12m;
        decimal[] closes = Enumerable.Range(0, 13).Select(index => first + step * index).ToArray();
        return new DirectionalMarketEvidence(bid, ask, closes);
    }

    private static PortfolioSnapshot Portfolio() => new(
        0, new Usd(100_000), new Usd(100_000), new Usd(100_000),
        Usd.Zero, Usd.Zero, Usd.Zero, Usd.Zero, 0, 0, 0, 0, 0, 0, 0, []);

    private sealed class LifecycleTrace
    {
        private readonly List<(string Stage, string Detail)> _stages = [];

        public bool ReachedBroker { get; set; }
        public string StoppedAt { get; set; } = "not stopped";
        public BrokerSubmitState? SubmitState { get; set; }
        public ExecutionCommand? SubmittedCommand { get; set; }
        public long ReservationId { get; set; }

        public void Add(string stage, string detail) => _stages.Add((stage, detail));

        public string Render()
        {
            var builder = new StringBuilder();
            builder.AppendLine("=== autonomous lifecycle trace ===");
            foreach ((string stage, string detail) in _stages)
                builder.AppendLine($"  {stage,-20} {detail}");
            builder.AppendLine($"  {"outcome",-20} " +
                (ReachedBroker ? "order reached the broker adapter" : $"stopped at {StoppedAt}"));
            return builder.ToString();
        }
    }

    private sealed class RecordingBroker : IBrokerExecutionGateway
    {
        public ExecutionCommand? LastCommand { get; private set; }

        public bool IsPaperEnvironment => true;

        public Task<BrokerSubmitResult> SubmitAsync(
            ExecutionCommand command, CancellationToken cancellationToken)
        {
            LastCommand = command;
            return Task.FromResult(new BrokerSubmitResult(
                BrokerSubmitState.Acknowledged, $"broker-{command.ClientOrderId}", null, "req-1"));
        }

        public Task<BrokerOrderSnapshot?> FindByClientOrderIdAsync(
            string clientOrderId, CancellationToken cancellationToken) =>
            Task.FromResult<BrokerOrderSnapshot?>(null);
    }
}
