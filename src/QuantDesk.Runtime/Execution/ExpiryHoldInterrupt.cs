using QuantDesk.Runtime.Time;

namespace QuantDesk.Runtime.Execution;

/// <summary>
/// Closes an options position before its contracts get too close to expiry.
///
/// A defined-risk vertical two days from expiry is not the position that was opened with weeks to
/// run. Gamma and the sensitivity of the payoff to small moves both rise sharply, the spread widens
/// as market makers step back, and assignment risk on the short leg becomes real. The maximum
/// holding period does not protect against any of that, because it is measured from entry and knows
/// nothing about the calendar the contracts are on.
///
/// <c>MinimumDteToHold</c> already existed on the management plan to express this. It was passed as
/// null by every compiler and read by nothing, so the rule was stated in the domain and absent from
/// the system. This is what enforces it.
/// </summary>
public sealed class ExpiryHoldInterrupt(IRuntimeClock clock, int minimumDaysToExpiry) : IHoldInterrupt
{
    public HoldInterrupt Evaluate(in HeldPosition position)
    {
        // Spot does not expire. Nothing to check, and no reason to treat it as though it did.
        if (position.EarliestLegExpiry is not { } expiry) return HoldInterrupt.None;

        double remaining = (expiry - clock.UtcNow).TotalDays;
        if (remaining > minimumDaysToExpiry) return HoldInterrupt.None;

        return HoldInterrupt.Now(
            $"ApproachingExpiry:{remaining:0.0}d<={minimumDaysToExpiry}d");
    }
}
