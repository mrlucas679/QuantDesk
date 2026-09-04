using QuantDesk.Alpaca.MarketData;
using QuantDesk.Runtime.Indicators;

namespace QuantDesk.Api.PaperTrading;

/// <summary>Whether an instrument moves enough to pay for trading it.</summary>
/// <param name="Viable">True when the expected move over the holding period clears the round trip.</param>
/// <param name="Reason">Why not, when it is not.</param>
/// <param name="ExpectedMoveBps">The typical move this instrument makes over the holding period.</param>
/// <param name="HurdleBps">Live spread plus fees, slippage, and the minimum net edge.</param>
public readonly record struct CostViability(
    bool Viable, string Reason, decimal ExpectedMoveBps, decimal HurdleBps);

/// <summary>
/// Asks whether an instrument can pay for a round trip at all, without assuming a direction.
///
/// What this replaces, and why
/// ---------------------------
/// Admission used to run through a gate whose first test was that recent momentum was positive and
/// exceeded the round trip. That is the momentum strategy's own entry condition. Used as a
/// universal pre-filter it silently made most of the strategy set unreachable: a mean-reversion
/// rule could only fire while prices were already rising, so buying a dip required the dip not to
/// have happened, and nine of thirteen strategies could never have opened a position.
///
/// The question that genuinely applies to every mechanism is different and simpler: does this
/// instrument typically move further than the round trip costs? A strategy of any direction needs
/// that to be true, and no strategy's direction is assumed by asking it.
///
/// It is a necessary condition, not a sufficient one. Clearing it says the instrument is not so
/// quiet that the fee eats any plausible move; it says nothing about whether the strategy is right.
/// The evidence gates decide that, and they have not yet said yes to anything.
/// </summary>
public sealed class CostViabilityGate(ExecutionCostProfile costs, int holdingBars)
{
    private const int MinimumCloses = 13;


    /// <summary>
    /// Scales a single bar's range to the holding period.
    ///
    /// Square root of time, because independent increments accumulate in variance rather than in
    /// range. Multiplying the bar range by the bar count would overstate a four-hour hold by
    /// roughly a factor of five and admit instruments that cannot pay their costs.
    /// </summary>
    private static decimal ScaleToHold(decimal perBarBps, int bars) =>
        perBarBps * (decimal)Math.Sqrt(Math.Max(bars, 1));

    public CostViability Evaluate(DirectionalMarketEvidence evidence, OpportunityRoute route)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(route);

        // Thirteen closes, matching the minimum the lane has always required. Raising it here would
        // silently refuse instruments the rest of the system considers adequately observed.
        if (evidence.Bid <= 0 || evidence.Ask < evidence.Bid || evidence.Closes.Count < MinimumCloses)
            return new(false, "INSUFFICIENT_FRESH_EVIDENCE", 0m, 0m);

        decimal mid = (evidence.Bid + evidence.Ask) / 2m;
        if (mid <= 0) return new(false, "INSUFFICIENT_FRESH_EVIDENCE", 0m, 0m);

        decimal spreadBps = (evidence.Ask - evidence.Bid) / mid * 10_000m;
        decimal hurdle = costs.HurdleBps(spreadBps);
        decimal expectedMove = ExpectedMoveBps(evidence, mid);

        if (expectedMove <= 0m)
            return new(false, "INSUFFICIENT_FRESH_EVIDENCE", expectedMove, hurdle);

        return expectedMove > hurdle
            ? new(true, "INSTRUMENT_CAN_PAY_ITS_COSTS", expectedMove, hurdle)
            : new(false, "EXPECTED_MOVE_BELOW_COSTS", expectedMove, hurdle);
    }

    /// <summary>
    /// The typical move this instrument makes over the holding period, in basis points.
    ///
    /// Measured from the average true range where full bars are available, and from the mean
    /// absolute close-to-close change otherwise. Both describe how far the instrument travels
    /// without claiming which way it will go -- which is the whole point of asking here rather than
    /// asking a directional question.
    /// </summary>
    private decimal ExpectedMoveBps(DirectionalMarketEvidence evidence, decimal mid)
    {
        if (evidence.HasFullBars &&
            IndicatorSet.Build(evidence.Closes, evidence.Highs, evidence.Lows, evidence.Volumes)
                is { } indicators &&
            indicators.IsReadyAt(indicators.Length - 1, indicators.Atr14))
        {
            decimal atr = (decimal)indicators.Atr14[^1];
            return ScaleToHold(atr / mid * 10_000m, holdingBars);
        }

        IReadOnlyList<decimal> closes = evidence.Closes;
        decimal total = 0m;
        for (int i = 1; i < closes.Count; i++) total += Math.Abs(closes[i] - closes[i - 1]);
        decimal perBar = total / (closes.Count - 1) / mid * 10_000m;
        return ScaleToHold(perBar, holdingBars);
    }
}
