using QuantDesk.Domain.Forecasts;
using QuantDesk.Domain.Scoring;
using QuantDesk.Runtime.Scoring;

namespace QuantDesk.Runtime.Tests.Scoring;

/// <summary>
/// The measured loss becoming the number the committee gates on.
///
/// Every expert reported a constant 0.5 against a committee floor of 0.5, so the gate passed by a
/// hair's breadth and could never refuse anything -- while the scorer measured QLIKE and Brier
/// against realised outcomes and nothing read the result.
///
/// The threshold has to mean something arguable, and these pin what it means.
/// </summary>
public sealed class ForecastCalibrationTests
{
    [Fact]
    public void APerfectForecastIsFullyCalibrated()
    {
        Assert.Equal(1d, ForecastCalibration.From(Scored(loss: 0d)), precision: 12);
    }

    [Fact]
    public void CalibrationFallsAsLossRises()
    {
        double better = ForecastCalibration.From(Scored(loss: 0.2d));
        double worse = ForecastCalibration.From(Scored(loss: 1.4d));

        Assert.True(better > worse);
        Assert.InRange(worse, 0d, 1d);
    }

    [Fact]
    public void TheDefaultFloorMeansAVarianceForecastWrongByAboutAFactorOfTwoAndAHalf()
    {
        // The claim the whole mapping rests on, checked rather than asserted in a comment. A
        // calibration of 0.5 is a QLIKE of ln(2); solving r - ln(r) - 1 = ln(2) gives the ratio of
        // realised to forecast variance at which the expert stops being worth sizing on.
        double lossAtFloor = ForecastCalibration.LossAt(0.5d);
        Assert.Equal(Math.Log(2d), lossAtFloor, precision: 12);

        double ratio = ForecastCalibration.VarianceRatioAt(lossAtFloor);
        Assert.InRange(ratio, 2.5d, 2.7d);
    }

    [Fact]
    public void TheVarianceRatioInvertsQLikeExactly()
    {
        // The reading is only useful if it is right. r - ln(r) - 1 evaluated at the returned ratio
        // must give back the loss it was asked about.
        foreach (double loss in new[] { 0.1d, 0.693d, 1d, 2.5d })
        {
            double ratio = ForecastCalibration.VarianceRatioAt(loss);
            Assert.Equal(loss, ratio - Math.Log(ratio) - 1d, precision: 6);
        }
    }

    [Fact]
    public void TheFirstMeasuredVolatilityScoreWouldSitJustBelowTheFloor()
    {
        // The live QLIKE measured over eighteen independent episodes was 0.697. That maps to just
        // under a half, so turning this on refuses the volatility expert -- which is the point, and
        // is why the recording path is deliberately not gated: it has to keep producing outcomes to
        // have any chance of earning its way back.
        double calibration = ForecastCalibration.From(Scored(loss: 0.697d));

        Assert.InRange(calibration, 0.49d, 0.50d);
    }

    [Fact]
    public void AnUnmeasuredExpertSitsExactlyAtTheFloorRatherThanAboveIt()
    {
        // Neither trusted nor refused. It passes while nothing is known and is refused the moment a
        // measurement says it should be; starting higher would grant confidence no one has earned.
        Assert.Equal(0.5d, ForecastCalibration.Unmeasured);
        Assert.Equal(ForecastCalibration.Unmeasured, ForecastCalibration.From(null));
    }

    [Fact]
    public void AScoreWithoutEnoughIndependentEpisodesIsNotUsed()
    {
        // A loss computed from a handful of overlapping windows is a number, not evidence. Letting
        // it drive a gate would refuse an expert on the strength of an afternoon.
        ExpertForecastScore thin = Scored(loss: 3d) with
        {
            Status = ScoreEvidenceStatus.InsufficientEvidence,
        };

        Assert.Equal(ForecastCalibration.Unmeasured, ForecastCalibration.From(thin));
    }

    [Fact]
    public void ANonFiniteOrNegativeLossIsNotUsed()
    {
        Assert.Equal(ForecastCalibration.Unmeasured, ForecastCalibration.From(Scored(double.NaN)));
        Assert.Equal(ForecastCalibration.Unmeasured, ForecastCalibration.From(Scored(-1d)));
        Assert.Equal(ForecastCalibration.Unmeasured, ForecastCalibration.From(Scored(null)));
    }

    [Fact]
    public void AnExpertIsJudgedByItsWorstRegimeRatherThanItsAverage()
    {
        // An expert well calibrated in calm and hopeless in stress is not half-calibrated. It is an
        // expert that fails exactly when it is needed, and averaging the two would hide that.
        var source = new MeasuredCalibrationSource();
        source.Refresh(
        [
            Scored(loss: 0.05d) with { Regime = "calm" },
            Scored(loss: 2.5d) with { Regime = "stress" },
        ]);

        Assert.Equal(
            ForecastCalibration.From(Scored(loss: 2.5d)),
            source.For(20, ForecastType.RealizedVolatility),
            precision: 12);
    }

    [Fact]
    public void AnExpertWithNoScoreYetGetsTheUnmeasuredDefault()
    {
        var source = new MeasuredCalibrationSource();
        source.Refresh([Scored(loss: 0.1d)]);

        Assert.Equal(ForecastCalibration.Unmeasured, source.For(99, ForecastType.RealizedVolatility));
        Assert.Equal(ForecastCalibration.Unmeasured, source.For(20, ForecastType.DirectionalReturn));
    }

    [Fact]
    public void RefreshingReplacesRatherThanAccumulates()
    {
        // A score that stops being measured -- because its outcomes aged out of the log -- must stop
        // being reported, not linger as the last good reading.
        var source = new MeasuredCalibrationSource();
        source.Refresh([Scored(loss: 0.05d)]);
        Assert.True(source.For(20, ForecastType.RealizedVolatility) > 0.9d);

        source.Refresh([]);
        Assert.Equal(ForecastCalibration.Unmeasured, source.For(20, ForecastType.RealizedVolatility));
    }

    private static ExpertForecastScore Scored(double? loss) => new(
        ExpertId: 20,
        ForecastType: ForecastType.RealizedVolatility,
        Regime: "all",
        PrimaryMetric: ForecastScoreMetric.QLike,
        Status: ScoreEvidenceStatus.Scored,
        SampleCount: 40,
        IndependentEpisodeCount: 18,
        PrimaryLoss: loss,
        MeanAbsoluteError: null,
        RootMeanSquaredError: null,
        BrierScore: null,
        QLike: loss,
        DirectionalAccuracy: null,
        CalibrationError: null);
}
