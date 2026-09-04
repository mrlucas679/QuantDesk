using QuantDesk.Runtime.Scoring;
using QuantDesk.Api.PaperTrading;
using QuantDesk.Domain.Forecasts;
using QuantDesk.Runtime.Experts;
using QuantDesk.Runtime.Indicators;
using QuantDesk.Runtime.Time;

namespace QuantDesk.Api.Tests;

/// <summary>
/// Typed forecasts now reach the runtime through the committee that keeps their families apart.
///
/// What this connection is, and what it is not
/// -------------------------------------------
/// Section 10.1 exists so that a volatility reading cannot become a direction, and
/// <c>TypedForecastCommittee</c> is where that separation is enforced. It was written, tested and
/// wired to nothing; this path published whatever an expert returned.
///
/// Honest about the effect today: with one expert per family the aggregation is a pass-through, and
/// two of the three gates cannot fire from this call site because the same timestamp and state
/// version feed both the expert and the committee. The gate that is live is calibration, and the
/// volatility expert reports a constant 0.5 against a threshold of 0.5 -- so it passes by a
/// hair's breadth and would refuse the moment a measured score replaced the constant.
///
/// That makes this a structural connection rather than a behaviour change, which is worth saying
/// plainly. What it buys is that the refusal path exists and is exercised: when a second expert
/// joins a family, or when the scorer's QLIKE replaces the hard-coded calibration, the aggregation
/// and the gate are already in the path rather than being bolted on afterwards.
/// </summary>
public sealed class IndicatorRegimeSourceCommitteeTests
{
    [Fact]
    public void AForecastThatClearsTheCommitteeIsPublished()
    {
        var source = Source(new TypedForecastCommittee());

        source.Observe("BTC/USD", Bars(), 0, EventNanoseconds, MonotonicTicks, sourceStateVersion: 0);

        Assert.NotNull(source.CurrentRegime("BTC/USD"));
    }

    [Fact]
    public void AForecastTheCommitteeRefusesIsNotPublished()
    {
        // The gate doing its job. A calibration floor above what any expert reports refuses every
        // forecast, which is the same path a genuinely badly-calibrated expert would take.
        var source = Source(new TypedForecastCommittee(minimumCalibrationScore: 0.99));

        source.Observe("BTC/USD", Bars(), 0, EventNanoseconds, MonotonicTicks, sourceStateVersion: 0);

        Assert.Null(source.CurrentRegime("BTC/USD"));
    }

    [Fact]
    public void ARefusalLeavesNoStaleRegimeBehind()
    {
        // A refusal must not leave the previous answer standing as though it were current. The exit
        // engine reads this, and a regime that stopped being published but kept being returned is
        // worse than one that was never published.
        var accepting = Source(new TypedForecastCommittee());
        accepting.Observe("BTC/USD", Bars(), 0, EventNanoseconds, MonotonicTicks, 0);
        Assert.NotNull(accepting.CurrentRegime("BTC/USD"));

        var refusing = Source(new TypedForecastCommittee(minimumCalibrationScore: 0.99));
        refusing.Observe("BTC/USD", Bars(), 0, EventNanoseconds, MonotonicTicks, 0);

        Assert.Null(refusing.CurrentRegime("BTC/USD"));
    }

    [Fact]
    public void AnUnknownSymbolHasNoRegimeRatherThanADefaultOne()
    {
        Assert.Null(Source(new TypedForecastCommittee()).CurrentRegime("ETH/USD"));
    }

    [Fact]
    public void TooLittleHistoryPublishesNothing()
    {
        // The expert declines below its long window, and the committee turns that into an absent
        // forecast rather than an empty one.
        //
        // An unwarmed set, because that is what short history actually produces: the builder
        // returns null below its warm-up and every derived series stays NaN, so the rules that read
        // one decline. Feeding a warmed set of a hundred bars would be testing a state the runtime
        // never reaches.
        var source = Source(new TypedForecastCommittee());
        IndicatorSet tooShort = IndicatorSet.Unwarmed(
            [.. Enumerable.Range(0, 100).Select(index => 30_000m + index)]);

        source.Observe("BTC/USD", tooShort, 0, EventNanoseconds, MonotonicTicks, 0);

        Assert.Null(source.CurrentRegime("BTC/USD"));
    }

    // ------------------------------------------------------------------------------- fixtures

    private const long EventNanoseconds = 1_770_000_000_000_000_000L;
    private const long MonotonicTicks = 1_000_000L;

    private static IndicatorRegimeSource Source(TypedForecastCommittee committee) =>
        new(new MarketRegimeExpert(), new RealizedVolatilityExpert(), committee,
            new MeasuredCalibrationSource(), new LiveRuntimeClock());

    /// <summary>
    /// Bars warmed through the real builder, because the regime expert reads derived series.
    ///
    /// An unwarmed set leaves every derived series NaN by design, so the regime expert declines and
    /// the test would pass for the wrong reason -- reporting nothing published whether the committee
    /// refused or the expert never spoke.
    /// </summary>
    private static IndicatorSet Bars(int bars = RealizedVolatilityExpert.LongBars + 120)
    {
        var closes = new List<decimal>(bars);
        var highs = new List<decimal>(bars);
        var lows = new List<decimal>(bars);
        var volumes = new List<decimal>(bars);

        double price = 30_000d;
        var random = new Random(23);

        for (int index = 0; index < bars; index++)
        {
            // Wider swings late, so short and long realised variance disagree and the regime has
            // something to classify.
            double scale = index > bars - 100 ? 0.005d : 0.0009d;
            price *= Math.Exp((random.NextDouble() - 0.5d) * scale);

            decimal close = (decimal)price;
            decimal range = close * (decimal)(scale / 2d);
            closes.Add(close);
            highs.Add(close + range);
            lows.Add(close - range);
            volumes.Add(1_000m + index);
        }

        return IndicatorSet.Build(closes, highs, lows, volumes)
            ?? throw new InvalidOperationException("Indicator warm-up failed for the fixture.");
    }
}
