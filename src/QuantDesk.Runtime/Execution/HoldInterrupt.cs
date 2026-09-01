using QuantDesk.Runtime.Persistence;

namespace QuantDesk.Runtime.Execution;

/// <summary>Why a hold should end before its scheduled time, or that it should not.</summary>
/// <param name="ShouldExitNow">True to bring the exit forward to now.</param>
/// <param name="Reason">Operator-readable cause, recorded on the execution.</param>
public readonly record struct HoldInterrupt(bool ShouldExitNow, string? Reason)
{
    public static readonly HoldInterrupt None = new(false, null);

    public static HoldInterrupt Now(string reason) => new(true, reason);
}

/// <summary>
/// A reason a held position should be closed before its timer expires.
///
/// Why this exists
/// ---------------
/// The durable spot lifecycle exited on one condition: the scheduled time. The richer exit rules —
/// thesis invalidation, regime change, and the defined maximum adverse loss — lived in an
/// <c>ExitEngine</c> that only the autonomous lane's in-memory position manager called, and that
/// manager was never invoked. So in the live path a position whose research had been retracted ran
/// to its timer, and a position moving hard against the account ran to its timer: the defined
/// maximum loss sized the capital reservation but was never acted upon as a stop.
///
/// The contract that keeps this safe
/// ---------------------------------
/// An interrupt may only bring the exit *forward*. It is consulted alongside the scheduled time and
/// never in place of it, so an implementation that throws, hangs, or wrongly returns "keep holding"
/// cannot extend a hold beyond the deadline the reservation was taken against. The timer remains
/// the backstop it always was; this only adds ways to leave sooner.
/// </summary>
public interface IHoldInterrupt
{
    /// <summary>Whether this held execution should exit now.</summary>
    HoldInterrupt Evaluate(SpotExecutionRecord record);
}

/// <summary>
/// Consults several interrupts and exits on the first that fires.
///
/// Order matters only for which reason gets recorded, not for whether the exit happens, so the more
/// specific causes are listed first to make the operator-facing reason the informative one.
/// </summary>
public sealed class CompositeHoldInterrupt(params IHoldInterrupt[] interrupts) : IHoldInterrupt
{
    private readonly IHoldInterrupt[] _interrupts = interrupts ?? [];

    public HoldInterrupt Evaluate(SpotExecutionRecord record)
    {
        foreach (IHoldInterrupt interrupt in _interrupts)
        {
            HoldInterrupt result = interrupt.Evaluate(record);
            if (result.ShouldExitNow) return result;
        }

        return HoldInterrupt.None;
    }
}
