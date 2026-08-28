using System.Diagnostics;
using QuantDesk.Domain.Execution;
using QuantDesk.Domain.Numerics;
using QuantDesk.Domain.Runtime;
using QuantDesk.Domain.Trading;
using QuantDesk.Runtime.Execution;
using QuantDesk.Runtime.Modes;
using QuantDesk.Runtime.Reservations;
using QuantDesk.Runtime.Tests.TestData;

namespace QuantDesk.Runtime.Tests.Execution;

public sealed class ExecutionWorkerTests
{
    [Fact]
    public async Task SubmitOneAsync_TimeoutRetainsReservationAndNeverRetries()
    {
        var broker = new ScriptedBrokerGateway(async cancellationToken =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new UnreachableException();
        });
        var fixture = CreateFixture(broker, TimeSpan.FromMilliseconds(20));

        BrokerSubmitResult result = await fixture.Worker.SubmitOneAsync(
            fixture.Intent, fixture.Command, 50, CancellationToken.None);

        Assert.Equal(BrokerSubmitState.Unknown, result.State);
        Assert.Equal(1, broker.SubmitCalls);
        Assert.Equal(ExecutionIntentState.Reconciling, fixture.Intent.State);
        Assert.Equal(ReservationStatus.Unknown, fixture.Ledger.Get(fixture.Command.RiskReservationId).Status);
        Assert.Equal(new Usd(100), fixture.Ledger.Snapshot().ReservedRisk);
    }

    [Fact]
    public async Task SubmitOneAsync_RejectionReleasesUnusedReservation()
    {
        var broker = new ScriptedBrokerGateway(_ => Task.FromResult(
            new BrokerSubmitResult(BrokerSubmitState.Rejected, null, "BROKER_REJECTED", "request-1")));
        var fixture = CreateFixture(broker, TimeSpan.FromSeconds(1));

        BrokerSubmitResult result = await fixture.Worker.SubmitOneAsync(
            fixture.Intent, fixture.Command, 50, CancellationToken.None);

        Assert.Equal(BrokerSubmitState.Rejected, result.State);
        Assert.Equal(ReservationStatus.Released, fixture.Ledger.Get(fixture.Command.RiskReservationId).Status);
        Assert.Equal(Usd.Zero, fixture.Ledger.Snapshot().ReservedRisk);
        Assert.Equal(new Usd(10_000), fixture.Ledger.Snapshot().BuyingPower);
    }

    [Fact]
    public async Task ApplyTradeUpdate_TransitionsAcknowledgedIntentToFilled()
    {
        var broker = new ScriptedBrokerGateway(_ => Task.FromResult(
            new BrokerSubmitResult(BrokerSubmitState.Acknowledged, "broker-1", null, "request-1")));
        var fixture = CreateFixture(broker, TimeSpan.FromSeconds(1));
        await fixture.Worker.SubmitOneAsync(fixture.Intent, fixture.Command, 50, CancellationToken.None);
        fixture.Worker.ApplyTradeUpdate(fixture.Intent,
            new BrokerTradeUpdate(BrokerTradeUpdateKind.Fill, fixture.Command.ClientOrderId, "broker-1", 1, 100, null, 1));
        Assert.Equal(ExecutionIntentState.Filled, fixture.Intent.State);
    }

    private static Fixture CreateFixture(IBrokerExecutionGateway broker, TimeSpan timeout)
    {
        var ledger = new ReservationLedger(FinancialTestData.Portfolio());
        Assert.True(ledger.TryReserve(0, new Usd(100), new Usd(500), new Usd(1_000), out PortfolioReservation? reservation));
        var mode = new RuntimeModeState();
        mode.Transition(SystemMode.Ready, "test preflight complete");
        var intent = new ExecutionIntent(1, 2, "trend");
        intent.TransitionTo(ExecutionIntentState.Approved);
        intent.AttachApproval("qd-campaign-trend-spy-1", reservation!.ReservationId, reservation.ReservationId);
        intent.TransitionTo(ExecutionIntentState.Queued);
        var command = new ExecutionCommand(
            1,
            ExecutionPriority.ExploitationEntry,
            reservation.ReservationId,
            reservation.ReservationId,
            intent.ClientOrderId!,
            0,
            OrderSide.Buy,
            PositionIntent.Open,
            ExecutionOrderType.Limit,
            ExecutionTimeInForce.Day,
            1,
            100,
            10,
            100,
            "trend");

        return new Fixture(new ExecutionWorker(broker, ledger, mode, timeout), ledger, intent, command);
    }

    private sealed record Fixture(
        ExecutionWorker Worker,
        ReservationLedger Ledger,
        ExecutionIntent Intent,
        ExecutionCommand Command);

    private sealed class ScriptedBrokerGateway(
        Func<CancellationToken, Task<BrokerSubmitResult>> submit) : IBrokerExecutionGateway
    {
        public int SubmitCalls { get; private set; }

        public Task<BrokerSubmitResult> SubmitAsync(
            ExecutionCommand command,
            CancellationToken cancellationToken)
        {
            SubmitCalls++;
            return submit(cancellationToken);
        }

        public Task<Domain.Execution.BrokerOrderSnapshot?> FindByClientOrderIdAsync(
            string clientOrderId,
            CancellationToken cancellationToken) => Task.FromResult<BrokerOrderSnapshot?>(null);
    }
}
