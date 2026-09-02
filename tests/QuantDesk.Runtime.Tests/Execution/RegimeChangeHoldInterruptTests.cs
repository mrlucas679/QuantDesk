using QuantDesk.Domain.Forecasts;
using QuantDesk.Runtime.Execution;

namespace QuantDesk.Runtime.Tests.Execution;

/// <summary>
/// The exit rule that had no input until the Regime family started being emitted.
///
/// ExitOnRegimeChange has been true on every candidate since the compiler was written and
/// ExitEngine has implemented it throughout. It could not be wired because the family was declared
/// and never produced, so the rule would have been reading a number nothing computed.
/// </summary>
public sealed class RegimeChangeHoldInterruptTests
{
    [Fact]
    public void StressClosesThePosition()
    {
        // The regime where the spread widens, the book thins, and the distribution the position was
        // sized against stops applying -- so the cost of staying rises faster than the cost of
        // leaving.
        HoldInterrupt interrupt = Evaluate(MarketRegime.Stress);

        Assert.True(interrupt.ShouldExitNow);
        Assert.Equal("RegimeChanged:Stress", interrupt.Reason);
    }

    [Theory]
    [InlineData(MarketRegime.LowVolTrend)]
    [InlineData(MarketRegime.HighVolTrend)]
    [InlineData(MarketRegime.Range)]
    [InlineData(MarketRegime.Event)]
    [InlineData(MarketRegime.Unknown)]
    public void EveryOtherRegimeLeavesThePositionAlone(MarketRegime regime)
    {
        // A trend that drifts into a range has not been invalidated, only made less likely to work,
        // and the timer already handles that. Exiting on every reclassification would close
        // positions constantly at the boundaries where the baseline is least certain, at 81.2 bps
        // a round trip -- measured, not assumed.
        Assert.False(Evaluate(regime).ShouldExitNow);
    }

    [Fact]
    public void NoRegimeMeansHold()
    {
        // A context expert that cannot speak is not evidence that the market has turned.
        Assert.False(Evaluate(null).ShouldExitNow);
    }

    [Fact]
    public void AnUnreadableSourceMeansHold()
    {
        // Closing on a source failure would convert every gap in the feed into a realised loss.
        Assert.False(
            new RegimeChangeHoldInterrupt(new ThrowingRegimes()).Evaluate(Position()).ShouldExitNow);
    }

    private static HoldInterrupt Evaluate(MarketRegime? regime) =>
        new RegimeChangeHoldInterrupt(new StubRegimes(regime)).Evaluate(Position());

    private static HeldPosition Position() => new(
        ExecutionId: "SPOT-1",
        Symbol: "AVAX/USD",
        Quantity: 28m,
        EntryPrice: 7.125m,
        DefinedMaximumLoss: 10m,
        Ownership: null,
        EarliestLegExpiry: null);

    private sealed class StubRegimes(MarketRegime? regime) : IRegimeSource
    {
        public MarketRegime? CurrentRegime(string symbol) => regime;
    }

    private sealed class ThrowingRegimes : IRegimeSource
    {
        public MarketRegime? CurrentRegime(string symbol) =>
            throw new InvalidOperationException("classifier unavailable");
    }
}
