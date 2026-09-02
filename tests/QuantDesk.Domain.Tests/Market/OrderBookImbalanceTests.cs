using QuantDesk.Domain.Market;

namespace QuantDesk.Domain.Tests.Market;

/// <summary>
/// Depth imbalance near the touch: the only evidence in this system not derived from price.
///
/// Every entry rule reads the same OHLCV series, which is a structural reason the book correlates
/// -- 0.709 mean pairwise across seven crypto pairs on 2026-09-02, about 1.33 independent bets held
/// as if they were seven.
/// </summary>
public sealed class OrderBookImbalanceTests
{
    [Fact]
    public void ABalancedBookReadsZero()
    {
        BookImbalance book = OrderBookImbalanceCalculator.Calculate(
            [new BookLevel(99.9m, 10m)], [new BookLevel(100.1m, 10m)]);

        Assert.Equal(0d, book.Imbalance, precision: 9);
        Assert.True(book.IsMeasurable);
    }

    [Fact]
    public void MoreRestingBidThanAskLeansPositive()
    {
        BookImbalance book = OrderBookImbalanceCalculator.Calculate(
            [new BookLevel(99.9m, 30m)], [new BookLevel(100.1m, 10m)]);

        Assert.Equal(0.5d, book.Imbalance, precision: 9);
    }

    [Fact]
    public void SizeOutsideTheBandIsExcludedRatherThanAveragedIn()
    {
        // The defect a whole-book sum would have. Alpaca answers with everything it has -- 85 bid
        // levels against 61 ask levels for BTC/USD in one response on 2026-09-02 -- so summing all
        // of it lets size resting percent away from the mid, which will not trade inside any
        // horizon this system holds for, decide a number meant to describe pressure at the touch.
        // It also makes the measure a function of how many levels the venue felt like sending.
        BookImbalance book = OrderBookImbalanceCalculator.Calculate(
            [new BookLevel(99.9m, 10m), new BookLevel(50m, 10_000m)],
            [new BookLevel(100.1m, 10m)],
            bandBps: 25d);

        Assert.Equal(0d, book.Imbalance, precision: 9);
        Assert.Equal(1, book.BidLevels);
    }

    [Fact]
    public void TheSameBandMeansTheSameThingAtAnyPrice()
    {
        // Measured as a fraction of the mid, so a 77,000-dollar instrument and a 7-dollar one are
        // described by the same rule rather than by whatever their absolute tick happens to be.
        BookImbalance expensive = OrderBookImbalanceCalculator.Calculate(
            [new BookLevel(77_000m, 5m), new BookLevel(76_000m, 100m)],
            [new BookLevel(77_100m, 5m)]);

        BookImbalance cheap = OrderBookImbalanceCalculator.Calculate(
            [new BookLevel(7.70m, 5m), new BookLevel(7.60m, 100m)],
            [new BookLevel(7.71m, 5m)]);

        Assert.Equal(expensive.BidLevels, cheap.BidLevels);
        Assert.Equal(expensive.Imbalance, cheap.Imbalance, precision: 6);
    }

    [Fact]
    public void AOneSidedBookIsNotMeasurable()
    {
        // The arithmetic gives exactly +1, which reads as maximal conviction and is usually just a
        // thin venue. The caller has to be able to tell those apart.
        BookImbalance book = OrderBookImbalanceCalculator.Calculate(
            [new BookLevel(99.9m, 10m)], []);

        Assert.False(book.IsMeasurable);
    }

    [Fact]
    public void ACrossedBookIsRefused()
    {
        // Bid above ask is not a tight book, it is bad data, and section 9.1 says it must never
        // become tradable state.
        Assert.False(
            OrderBookImbalanceCalculator
                .Calculate([new BookLevel(101m, 10m)], [new BookLevel(100m, 10m)])
                .IsMeasurable);
    }

    [Fact]
    public void ImbalanceStaysInsideMinusOneToOne()
    {
        BookImbalance book = OrderBookImbalanceCalculator.Calculate(
            [new BookLevel(99.9m, 1_000m)], [new BookLevel(100.1m, 0.0001m)]);

        Assert.InRange(book.Imbalance, -1d, 1d);
    }
}
