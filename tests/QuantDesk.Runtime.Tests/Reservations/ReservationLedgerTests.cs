using QuantDesk.Domain.Numerics;
using QuantDesk.Runtime.Reservations;
using QuantDesk.Runtime.Tests.TestData;

namespace QuantDesk.Runtime.Tests.Reservations;

public sealed class ReservationLedgerTests
{
    [Fact]
    public async Task ConcurrentReservations_CannotDoubleSpendRiskCapacity()
    {
        var ledger = new ReservationLedger(FinancialTestData.Portfolio(buyingPower: 1_000));
        using var start = new ManualResetEventSlim(false);

        Task<bool> first = Task.Run(() => ReserveAfterSignal(ledger, start));
        Task<bool> second = Task.Run(() => ReserveAfterSignal(ledger, start));
        start.Set();

        bool[] results = await Task.WhenAll(first, second);

        Assert.Single(results, result => result);
        Assert.Equal(new Usd(700), ledger.Snapshot().ReservedRisk);
        Assert.Equal(new Usd(300), ledger.Snapshot().BuyingPower);
    }

    [Fact]
    public void UnknownReservation_RemainsActive()
    {
        var ledger = new ReservationLedger(FinancialTestData.Portfolio());
        Assert.True(ledger.TryReserve(0, new Usd(100), new Usd(500), new Usd(1_000), out PortfolioReservation? reservation));

        ledger.MarkUnknown(reservation!.ReservationId);

        Assert.True(ledger.IsActive(reservation.ReservationId));
        Assert.Equal(ReservationStatus.Unknown, ledger.Get(reservation.ReservationId).Status);
        Assert.Equal(new Usd(100), ledger.Snapshot().ReservedRisk);
    }

    private static bool ReserveAfterSignal(ReservationLedger ledger, ManualResetEventSlim start)
    {
        start.Wait();
        return ledger.TryReserve(0, new Usd(700), new Usd(700), new Usd(1_000), out _);
    }
}

