namespace QuantDesk.Domain.Forecasts;

/// <summary>
/// Three different questions that were being answered with one number.
///
/// The conflation
/// --------------
/// A model's point forecast for the current bar flowed straight through to <c>GrossExpectedPnl</c>
/// and was compared against modelled cost. That single number was silently asked to answer three
/// separate questions:
///
/// <list type="number">
/// <item><description>
/// <b>The current signal.</b> What the model says about this bar, right now. It is the only one of
/// the three that is about now, and on its own it says nothing about whether the model is any good.
/// </description></item>
/// <item><description>
/// <b>The historical net edge.</b> What this family actually earned, after costs, across the
/// sample research validated it on. A family with no demonstrated net edge does not acquire one
/// because today's signal is large.
/// </description></item>
/// <item><description>
/// <b>The forecast distribution.</b> How much the current signal could be wrong by. A point
/// forecast of +80 bps means something entirely different when its standard error is 5 bps than
/// when it is 200.
/// </description></item>
/// </list>
///
/// Treating (1) as though it were (2), with (3) absent, trades a large noisy reading from a family
/// that has never made money — which is precisely the failure mode this system's own results show.
///
/// What is required instead
/// ------------------------
/// Both questions must be answered positively, and both against uncertainty rather than against a
/// point. The current signal must clear costs at its <em>lower</em> confidence bound, and the
/// family's historical net edge must itself be positive at its lower bound. Costs enter at their
/// <em>upper</em> bound, because the estimate's own error must count against trading rather than
/// for it.
/// </summary>
/// <param name="CurrentSignalBps">The model's point forecast for this bar.</param>
/// <param name="CurrentSignalStandardErrorBps">
/// Dispersion of that forecast. Null when the publisher did not state one, which is not the same as
/// zero — see <see cref="ForecastEdgeAssessment"/>, which refuses rather than assuming certainty.
/// </param>
/// <param name="HistoricalNetEdgeBps">What the family earned per trade, net of costs, in research.</param>
/// <param name="HistoricalNetEdgeStandardErrorBps">Standard error of that historical mean.</param>
/// <param name="HistoricalObservations">Trades behind the historical figure.</param>
public readonly record struct ForecastEdge(
    double CurrentSignalBps,
    double? CurrentSignalStandardErrorBps,
    double? HistoricalNetEdgeBps,
    double? HistoricalNetEdgeStandardErrorBps,
    int? HistoricalObservations)
{
    /// <summary>One-sided 95% normal quantile.</summary>
    public const double OneSidedNinetyFivePercent = 1.645;

    /// <summary>
    /// Fewest trades a historical net edge may rest on and still be quoted.
    ///
    /// Thirty is the conventional point at which a sample mean's distribution is usable, not a
    /// claim that thirty trades prove anything. Below it the standard error is so wide that the
    /// lower bound would reject everything anyway; the explicit refusal simply says why.
    /// </summary>
    public const int MinimumHistoricalObservations = 30;

    /// <summary>The current signal, discounted for how wrong it could be.</summary>
    public double? CurrentSignalLowerBoundBps => CurrentSignalStandardErrorBps is { } error && error >= 0
        ? CurrentSignalBps - (OneSidedNinetyFivePercent * error)
        : null;

    /// <summary>The family's demonstrated net edge, discounted for sampling error.</summary>
    public double? HistoricalNetEdgeLowerBoundBps =>
        HistoricalNetEdgeBps is { } edge && HistoricalNetEdgeStandardErrorBps is { } error && error >= 0
            ? edge - (OneSidedNinetyFivePercent * error)
            : null;
}

/// <summary>Whether an edge survives its own uncertainty and the cost of capturing it.</summary>
/// <param name="Tradable">True only when every question below was answered positively.</param>
/// <param name="Reason">Which question failed, or "Tradable".</param>
/// <param name="CurrentSignalLowerBoundBps">The discounted signal, when one could be computed.</param>
/// <param name="HistoricalNetEdgeLowerBoundBps">The discounted historical edge, when one could be computed.</param>
public readonly record struct ForecastEdgeAssessment(
    bool Tradable,
    string Reason,
    double? CurrentSignalLowerBoundBps,
    double? HistoricalNetEdgeLowerBoundBps)
{
    /// <summary>
    /// Applies the full test: signal, history, and cost, each against its bound.
    ///
    /// Missing uncertainty is refused rather than treated as certainty. A publisher that omits the
    /// standard error has not told us the forecast is exact, it has told us nothing — and reading
    /// silence as zero error is how a point forecast came to be traded as though it were a fact.
    /// </summary>
    /// <param name="edge">The three quantities, separately stated.</param>
    /// <param name="allInCostUpperBoundBps">Measured cost at its upper confidence bound.</param>
    public static ForecastEdgeAssessment Evaluate(in ForecastEdge edge, double allInCostUpperBoundBps)
    {
        if (edge.CurrentSignalStandardErrorBps is null)
            return Reject("ForecastUncertaintyNotPublished", edge);
        if (edge.HistoricalNetEdgeBps is null || edge.HistoricalNetEdgeStandardErrorBps is null)
            return Reject("HistoricalNetEdgeNotPublished", edge);
        if (edge.HistoricalObservations is not { } observations)
            return Reject("HistoricalObservationCountNotPublished", edge);
        if (observations < ForecastEdge.MinimumHistoricalObservations)
            return Reject("HistoricalSampleTooSmall", edge);

        // The family must have demonstrated a net edge at all. Today's signal cannot supply one.
        if (edge.HistoricalNetEdgeLowerBoundBps is not { } historicalBound || historicalBound <= 0)
            return Reject("NoDemonstratedHistoricalEdge", edge);

        // And the signal must clear the cost of acting on it, discounted for its own error.
        if (edge.CurrentSignalLowerBoundBps is not { } signalBound)
            return Reject("ForecastUncertaintyNotPublished", edge);
        if (signalBound <= allInCostUpperBoundBps)
            return Reject("SignalBelowCostAtLowerBound", edge);

        return new(true, "Tradable", signalBound, historicalBound);
    }

    private static ForecastEdgeAssessment Reject(string reason, in ForecastEdge edge) =>
        new(false, reason, edge.CurrentSignalLowerBoundBps, edge.HistoricalNetEdgeLowerBoundBps);
}
