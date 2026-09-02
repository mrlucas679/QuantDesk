namespace QuantDesk.Runtime.Costs;

/// <summary>How a fill is assumed to have happened, from most to least generous.</summary>
public enum FillAssumption
{
    /// <summary>Filled at the mid. What an optimistic simulator gives and no taker ever gets.</summary>
    MidpointFavourable,

    /// <summary>Filled halfway across the spread. The usual charitable assumption.</summary>
    PartialSpread,

    /// <summary>Filled at the far touch on both legs. What a marketable order actually pays.</summary>
    AdverseFullSpread,

    /// <summary>Rested and was not filled, or was filled only when the market had already moved.</summary>
    MakerAdverseSelection,
}

/// <summary>
/// How much a reported result can be trusted, given the data and the fill behind it.
///
/// Not a measure of profit. A grade A loss is better evidence than a grade D gain, because the
/// question this answers is whether the number describes trading or describes the simulator.
/// </summary>
public enum SimulationQuality
{
    /// <summary>Tight book, healthy feed, size the touch could absorb. The number means what it says.</summary>
    A,

    /// <summary>One term degraded. Directionally trustworthy, not precise.</summary>
    B,

    /// <summary>Two or more terms degraded. Read as an indication, never as a measurement.</summary>
    C,

    /// <summary>The fill could not have happened as reported. Excluded from any conclusion.</summary>
    D,
}

/// <param name="Assumption">Which fill was assumed.</param>
/// <param name="CostBps">All-in round-trip cost under that assumption, in basis points.</param>
/// <param name="AdjustedPnl">What the episode would have earned under it.</param>
public readonly record struct FillScenario(FillAssumption Assumption, double CostBps, decimal AdjustedPnl);

/// <param name="Grade">How much the reported result can be trusted.</param>
/// <param name="Reasons">Every term that degraded it, so the grade is auditable rather than asserted.</param>
public readonly record struct SimulationGrade(SimulationQuality Grade, IReadOnlyList<string> Reasons);

/// <summary>
/// Prices one round trip under several fill assumptions, and grades how much the paper result can
/// be trusted.
///
/// Why one number is not enough
/// ----------------------------
/// Section 14.2 asks for paper P&amp;L kept separate from a realism-adjusted figure, several fill
/// assumptions run side by side, and a quality grade on every trade. Everything this system has
/// reported so far is a single fill assumption with no grade: the broker's paper engine filled, and
/// that fill was taken as the answer. It is not the answer, it is one draw from a distribution
/// whose spread is larger than most of the edges being argued about.
///
/// Today's evidence makes the point. The one round trip measured end to end cost 81.2 bps all-in
/// while the price moved 46.6 bps in the rule's favour -- the account still lost. Against edges
/// measured in single-digit basis points, the difference between filling at the mid and filling at
/// the far touch decides the sign of the result. Reporting one of those and calling it the outcome
/// is not a small imprecision.
///
/// What the grade is for
/// ---------------------
/// A grade is not a measure of profit. A grade A loss is better evidence than a grade D gain,
/// because the question is whether the number describes trading or describes the simulator. Every
/// term that degrades a grade is listed, so it can be argued with rather than trusted.
/// </summary>
public static class FillRealism
{
    /// <summary>
    /// Round-trip venue fee in basis points, charged whatever the fill assumption.
    ///
    /// Fifty, measured rather than read from a schedule: the venue takes its crypto fee in kind, so
    /// the entry-side rate is directly observable as the shortfall between what an order bought and
    /// what the account could then sell. Median 25.0 bps per entry across 62 matched round trips on
    /// 2026-09-02.
    /// </summary>
    public const double VenueFeeRoundTripBps = 50d;

    /// <summary>
    /// Extra cost attributed to resting and being adversely selected, in spreads.
    ///
    /// A maker order that fills has usually filled because the market came to it, which is the half
    /// of the distribution where being filled was the wrong outcome. One full spread is the
    /// conventional penalty and is deliberately unfitted -- nothing here has measured it.
    /// </summary>
    public const double AdverseSelectionSpreads = 1.0d;

    /// <summary>
    /// Prices the round trip under every assumption.
    /// </summary>
    /// <param name="relativeSpread">Quoted spread as a fraction of the mid, at decision time.</param>
    /// <param name="notional">Traded notional in USD.</param>
    /// <param name="frictionlessPnl">What the reference-price move alone would have earned.</param>
    public static IReadOnlyList<FillScenario> Scenarios(
        double relativeSpread,
        decimal notional,
        decimal frictionlessPnl)
    {
        if (!double.IsFinite(relativeSpread) || relativeSpread < 0d) relativeSpread = 0d;
        if (notional <= 0m) return [];

        double spreadBps = relativeSpread * 10_000d;

        // Spread is paid on entry and on exit, so a full-spread assumption costs two of them and a
        // half-spread assumption costs one. The mid assumption costs none, which is exactly why it
        // is not a fill.
        return
        [
            Scenario(FillAssumption.MidpointFavourable, VenueFeeRoundTripBps, notional, frictionlessPnl),
            Scenario(FillAssumption.PartialSpread, VenueFeeRoundTripBps + spreadBps, notional, frictionlessPnl),
            Scenario(FillAssumption.AdverseFullSpread, VenueFeeRoundTripBps + (2d * spreadBps), notional, frictionlessPnl),
            Scenario(
                FillAssumption.MakerAdverseSelection,
                VenueFeeRoundTripBps + (spreadBps * (2d + AdverseSelectionSpreads)),
                notional,
                frictionlessPnl),
        ];
    }

    /// <summary>
    /// What the reported paper result understates, in USD.
    ///
    /// Measured against the adverse full-spread case rather than the worst one. A marketable order
    /// really does cross the spread on both legs, so that is the honest baseline for a taker lane;
    /// the maker case describes a strategy this system does not run and would overstate the
    /// adjustment if used here.
    /// </summary>
    public static decimal AdditionalRealismCost(
        double relativeSpread,
        decimal notional,
        decimal reportedPnl)
    {
        IReadOnlyList<FillScenario> scenarios = Scenarios(relativeSpread, notional, reportedPnl);
        if (scenarios.Count == 0) return 0m;

        FillScenario adverse = scenarios.First(s => s.Assumption is FillAssumption.AdverseFullSpread);
        FillScenario midpoint = scenarios.First(s => s.Assumption is FillAssumption.MidpointFavourable);

        decimal difference = midpoint.AdjustedPnl - adverse.AdjustedPnl;
        return difference > 0m ? difference : 0m;
    }

    /// <summary>
    /// How far a reported result can be trusted, and every reason it cannot be trusted further.
    /// </summary>
    /// <param name="relativeSpread">Quoted spread as a fraction of the mid.</param>
    /// <param name="quoteHealthy">Whether the feed considered the quote usable.</param>
    /// <param name="volumeCoverage">Share of bars on which the feed reported volume, in [0,1].</param>
    /// <param name="notional">Traded notional in USD.</param>
    /// <param name="restingDepthNotional">
    /// Notional resting near the touch, or null when the book was not read.
    /// </param>
    public static SimulationGrade Grade(
        double relativeSpread,
        bool quoteHealthy,
        double volumeCoverage,
        decimal notional,
        decimal? restingDepthNotional)
    {
        List<string> reasons = [];

        // A fill reported against an unusable quote did not happen at a price anyone could have
        // traded at, so nothing downstream should read it as an outcome.
        if (!quoteHealthy) reasons.Add("quote was not healthy at decision time");

        double spreadBps = relativeSpread * 10_000d;
        if (spreadBps > 50d) reasons.Add($"spread {spreadBps:0.0} bps exceeds a full round trip");
        else if (spreadBps > 20d) reasons.Add($"spread {spreadBps:0.0} bps is wide against the fee");

        // Measured on 2026-09-02: 65.6% of crypto bars report no volume at all, from 12.6% on
        // BTC/USD to 91.3% on BCH/USD. A result built on a series that is mostly absent is not a
        // precise result about a quiet market.
        if (volumeCoverage < 0.5d)
            reasons.Add($"feed reported volume on only {volumeCoverage:P0} of bars");

        // Size the touch could not absorb would have walked the book, and a paper engine that fills
        // it at the touch is describing a trade that could not have happened as reported.
        if (restingDepthNotional is { } depth && depth > 0m && notional > depth)
            reasons.Add($"order of {notional:0.00} exceeds {depth:0.00} resting near the touch");

        if (!quoteHealthy) return new SimulationGrade(SimulationQuality.D, reasons);

        SimulationQuality grade = reasons.Count switch
        {
            0 => SimulationQuality.A,
            1 => SimulationQuality.B,
            _ => SimulationQuality.C,
        };

        return new SimulationGrade(grade, reasons);
    }

    private static FillScenario Scenario(
        FillAssumption assumption, double costBps, decimal notional, decimal frictionlessPnl)
    {
        decimal cost = notional * (decimal)(costBps / 10_000d);
        return new FillScenario(assumption, costBps, frictionlessPnl - cost);
    }
}
