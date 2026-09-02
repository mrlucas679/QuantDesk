using QuantDesk.Domain.Forecasts;
using QuantDesk.Domain.Market;
using QuantDesk.Domain.Numerics;

namespace QuantDesk.Runtime.Experts;

/// <summary>
/// Publishes a microstructure forecast from resting depth near the touch.
///
/// The hypothesis, stated so it can be falsified
/// ---------------------------------------------
/// More size resting on the bid than the ask, close enough to the touch to be consumed within the
/// horizon, precedes a small upward move. It is a pressure argument, not a value one: it says
/// nothing about what the instrument is worth and everything about which side runs out of patience
/// first. It fails when the resting size is not real -- spoofed, iceberged, or simply cancelled
/// faster than a taker can reach it -- and on a venue thin enough that one participant is the book.
///
/// Why this expert matters more than its own edge
/// ----------------------------------------------
/// Every one of the thirteen entry rules reads the same OHLCV series. Measured on 2026-09-02 the
/// seven traded crypto pairs had a mean pairwise correlation of 0.709: about 1.33 independent bets
/// held as if they were seven. Depth is the only evidence available that is not derived from price,
/// so even a weak signal here is worth more per unit of edge than a strong one that moves with
/// everything already in the book.
///
/// What it refuses to claim
/// ------------------------
/// Section 16.2 requires this to survive spread, maker/taker fees, fill uncertainty and adverse
/// selection before it means anything, and none of that has been established. So the expected
/// return published here is deliberately small and explicitly a candidate: the committee weighs it,
/// the cost gate charges it, and the risk governor can still refuse it. A one-sided book publishes
/// nothing at all rather than the maximal conviction its arithmetic would otherwise produce.
/// </summary>
public sealed class OrderBookImbalanceExpert
{
    /// <summary>
    /// Imbalance below which the book is treated as balanced.
    ///
    /// Depth wanders by a few percent on any liquid pair without meaning anything -- BTC/USD read
    /// -0.031 and ETH/USD +0.009 on two consecutive calls with nothing happening. Publishing those
    /// as forecasts would fill the committee with noise wearing a forecast's clothes.
    /// </summary>
    public const double MinimumImbalance = 0.15d;

    /// <summary>
    /// Basis points of expected move at a fully one-sided book, scaled linearly by imbalance.
    ///
    /// Ten. Chosen to be smaller than a single crossing of the spread rather than fitted to
    /// anything: nothing here has been measured, and an unmeasured expert that publishes a large
    /// number would dominate a committee it has not earned a place in.
    /// </summary>
    public const double MaximumExpectedMoveBps = 10d;

    /// <summary>
    /// A forecast, or null when the book cannot support one.
    /// </summary>
    public MicrostructureForecast? Forecast(
        in BookImbalance book,
        int instrumentSlot,
        int expertId,
        TimeSpan horizon,
        long eventNs,
        long nowMonotonicTicks,
        long validUntilMonotonicTicks,
        long sourceStateVersion)
    {
        // A one-sided book reads as maximal conviction and is usually just a thin venue.
        if (!book.IsMeasurable) return null;
        if (!double.IsFinite(book.Imbalance)) return null;
        if (Math.Abs(book.Imbalance) < MinimumImbalance) return null;

        double imbalance = Math.Clamp(book.Imbalance, -1d, 1d);

        // Fill probability falls as the book leans away from the side being taken: a taker buying
        // into a bid-heavy book is competing with resting bids, not being filled by them.
        double fill = Math.Clamp(0.5d + (imbalance * 0.25d), 0.05d, 0.95d);

        // Adverse selection is worst where the book is thinnest, because that is where the resting
        // size most likely belongs to someone who knows something.
        double depth = (double)(book.BidDepth + book.AskDepth);
        double adverseSelection = depth > 0d ? Math.Clamp(1d / (1d + depth), 0d, 1d) : 1d;

        return new MicrostructureForecast(
            new ForecastMetadata(
                expertId,
                instrumentSlot,
                ForecastType.Microstructure,
                horizon,
                eventNs,
                nowMonotonicTicks,
                validUntilMonotonicTicks,
                sourceStateVersion,
                ModelVersion: 1,
                ForecastStatus.Valid),
            imbalance,
            imbalance * MaximumExpectedMoveBps,
            new Probability(fill),
            adverseSelection,

            // Calibration is unmeasured, and saying so is the point. A committee that weighs by
            // calibration must not be handed a confident number by an expert that has never been
            // scored; half is the value that claims nothing either way.
            CalibrationScore: 0.5d);
    }
}
