using QuantDesk.Domain.Numerics;
using QuantDesk.Domain.Portfolio;

namespace QuantDesk.Runtime.Reservations;

public enum ReservationStatus
{
    Reserved,
    PartiallyCommitted,
    Committed,
    Released,
    Unknown
}

public sealed record PortfolioReservation(
    long ReservationId,
    Usd OriginalRisk,
    Usd OriginalCapital,
    Usd RemainingRisk,
    Usd RemainingCapital,
    ReservationStatus Status);

public sealed class ReservationLedger
{
    private readonly Lock _gate = new();
    private readonly Dictionary<long, PortfolioReservation> _reservations = [];
    private PortfolioSnapshot _snapshot;
    private long _nextReservationId;

    public ReservationLedger(PortfolioSnapshot initialSnapshot)
    {
        _snapshot = initialSnapshot;
    }

    public PortfolioSnapshot Snapshot()
    {
        lock (_gate) return _snapshot;
    }

    public bool TryReserve(
        long expectedVersion,
        Usd risk,
        Usd capital,
        Usd maximumOpenRisk,
        out PortfolioReservation? reservation)
    {
        if (risk.Value <= 0 || capital.Value <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(risk), "Risk and capital reservations must be positive.");
        }

        lock (_gate)
        {
            Usd projectedRisk = _snapshot.OpenRisk + _snapshot.ReservedRisk + risk;
            if (_snapshot.Version != expectedVersion || projectedRisk > maximumOpenRisk || capital > _snapshot.BuyingPower)
            {
                reservation = null;
                return false;
            }

            reservation = new PortfolioReservation(
                ++_nextReservationId,
                risk,
                capital,
                risk,
                capital,
                ReservationStatus.Reserved);
            _reservations.Add(reservation.ReservationId, reservation);
            _snapshot = _snapshot with
            {
                Version = _snapshot.Version + 1,
                ReservedRisk = _snapshot.ReservedRisk + risk,
                BuyingPower = _snapshot.BuyingPower - capital
            };
            return true;
        }
    }

    public bool IsActive(long reservationId)
    {
        lock (_gate)
        {
            return _reservations.TryGetValue(reservationId, out PortfolioReservation? reservation) &&
                reservation.Status is ReservationStatus.Reserved or ReservationStatus.PartiallyCommitted or ReservationStatus.Unknown;
        }
    }

    public void MarkUnknown(long reservationId)
    {
        lock (_gate)
        {
            PortfolioReservation reservation = GetReservation(reservationId);
            _reservations[reservationId] = reservation with { Status = ReservationStatus.Unknown };
        }
    }

    public void Release(long reservationId)
    {
        lock (_gate)
        {
            PortfolioReservation reservation = GetReservation(reservationId);
            if (reservation.Status == ReservationStatus.Unknown)
            {
                throw new InvalidOperationException("An unknown reservation cannot be released before reconciliation.");
            }

            if (reservation.Status == ReservationStatus.Released) return;
            if (reservation.Status == ReservationStatus.Committed)
            {
                throw new InvalidOperationException("A committed reservation cannot be released.");
            }

            _reservations[reservationId] = reservation with
            {
                RemainingRisk = Usd.Zero,
                RemainingCapital = Usd.Zero,
                Status = ReservationStatus.Released
            };
            _snapshot = _snapshot with
            {
                Version = _snapshot.Version + 1,
                ReservedRisk = _snapshot.ReservedRisk - reservation.RemainingRisk,
                BuyingPower = _snapshot.BuyingPower + reservation.RemainingCapital
            };
        }
    }

    public PortfolioReservation Get(long reservationId)
    {
        lock (_gate) return GetReservation(reservationId);
    }

    private PortfolioReservation GetReservation(long reservationId) =>
        _reservations.TryGetValue(reservationId, out PortfolioReservation? reservation)
            ? reservation
            : throw new KeyNotFoundException($"Reservation '{reservationId}' does not exist.");
}
