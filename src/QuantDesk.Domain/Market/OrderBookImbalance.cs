namespace QuantDesk.Domain.Market;

/// <summary>One side of a book at one price.</summary>
/// <param name="Price">The level's price.</param>
/// <param name="Size">Quantity resting there.</param>
public readonly record struct BookLevel(decimal Price, decimal Size);

/// <summary>
/// Depth imbalance near the touch, and how much of the book it was measured over.
/// </summary>
/// <param name="Imbalance">
/// (bid depth - ask depth) / (bid depth + ask depth), in [-1, 1]. Positive means more resting size
/// on the bid, which is the direction the handbook treats as a candidate predictor.
/// </param>
/// <param name="BidDepth">Resting quantity on the bid inside the measured band.</param>
/// <param name="AskDepth">Resting quantity on the ask inside the measured band.</param>
/// <param name="BandBps">How far from the mid the band reached.</param>
/// <param name="BidLevels">Bid levels inside the band.</param>
/// <param name="AskLevels">Ask levels inside the band.</param>
public readonly record struct BookImbalance(
    double Imbalance,
    decimal BidDepth,
    decimal AskDepth,
    double BandBps,
    int BidLevels,
    int AskLevels)
{
    /// <summary>
    /// True when both sides had something inside the band, so the ratio means what it says.
    ///
    /// A one-sided book gives an imbalance of exactly +1 or -1, which reads as maximal conviction
    /// and is usually just a thin venue. The caller should decline rather than treat it as signal.
    /// </summary>
    public bool IsMeasurable => BidLevels > 0 && AskLevels > 0 && BidDepth > 0m && AskDepth > 0m;
}

/// <summary>
/// Computes depth imbalance over a band around the mid rather than over the whole book.
///
/// Why a band, and not the whole book
/// ----------------------------------
/// The handbook writes the measure as (BidDepth - AskDepth) / (BidDepth + AskDepth) without saying
/// how deep to look, and the venue answers with everything: measured on 2026-09-02, Alpaca returned
/// 85 bid levels and 61 ask levels for BTC/USD in one response. Summing all of them lets size resting
/// percent away from the mid -- which will not trade inside any horizon this system holds for --
/// dominate a number that is supposed to describe pressure at the touch. It also makes the measure
/// a function of how many levels the venue felt like sending, which changes between calls.
///
/// Bounding the band by distance from the mid fixes both: the same band means the same thing on a
/// 77,000-dollar instrument and a 7-dollar one, and levels the horizon cannot reach are excluded
/// rather than averaged in.
///
/// This is a candidate predictor and nothing more. Section 16.2 is explicit that it must survive
/// spread, maker/taker fees, fill uncertainty and adverse selection before it means anything, and
/// nothing here asserts that it has.
/// </summary>
public static class OrderBookImbalanceCalculator
{
    /// <summary>
    /// How far from the mid the band reaches, in basis points.
    ///
    /// Twenty-five. Wide enough to hold several levels on a liquid pair, narrow enough that what it
    /// sums is size a move within the holding period could actually consume.
    /// </summary>
    public const double DefaultBandBps = 25d;

    public static BookImbalance Calculate(
        IReadOnlyList<BookLevel> bids,
        IReadOnlyList<BookLevel> asks,
        double bandBps = DefaultBandBps)
    {
        ArgumentNullException.ThrowIfNull(bids);
        ArgumentNullException.ThrowIfNull(asks);

        if (bids.Count == 0 || asks.Count == 0) return Empty(bandBps);

        decimal bestBid = 0m;
        foreach (BookLevel level in bids)
        {
            if (level.Price > bestBid && level.Size > 0m) bestBid = level.Price;
        }

        decimal bestAsk = decimal.MaxValue;
        foreach (BookLevel level in asks)
        {
            if (level.Price < bestAsk && level.Size > 0m) bestAsk = level.Price;
        }

        if (bestBid <= 0m || bestAsk == decimal.MaxValue || bestAsk <= bestBid) return Empty(bandBps);

        decimal mid = (bestBid + bestAsk) / 2m;
        decimal band = mid * (decimal)(bandBps / 10_000d);

        decimal bidDepth = 0m;
        int bidLevels = 0;
        foreach (BookLevel level in bids)
        {
            if (level.Size <= 0m || level.Price <= 0m) continue;
            if (mid - level.Price > band) continue;
            bidDepth += level.Size;
            bidLevels++;
        }

        decimal askDepth = 0m;
        int askLevels = 0;
        foreach (BookLevel level in asks)
        {
            if (level.Size <= 0m || level.Price <= 0m) continue;
            if (level.Price - mid > band) continue;
            askDepth += level.Size;
            askLevels++;
        }

        decimal total = bidDepth + askDepth;
        double imbalance = total > 0m ? (double)((bidDepth - askDepth) / total) : 0d;

        return new BookImbalance(imbalance, bidDepth, askDepth, bandBps, bidLevels, askLevels);
    }

    private static BookImbalance Empty(double bandBps) => new(0d, 0m, 0m, bandBps, 0, 0);
}
