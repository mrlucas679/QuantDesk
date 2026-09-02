using QuantDesk.Domain.Contracts;
using QuantDesk.Runtime.Experts;

namespace QuantDesk.Runtime.Tests.Experts;

/// <summary>
/// The one model that crosses the Python boundary, and the refusals that keep it honest.
///
/// Section 20.3 puts HAR in C# because its inference is a dot product of four numbers, so porting
/// it means copying four doubles rather than reimplementing a model. An HMM's filtering and a
/// gradient-boosted traversal are not four numbers, and a hand-rolled approximation of either would
/// be worse than not having it.
/// </summary>
public sealed class HarVarianceModelTests
{
    private const string RuntimeHash = "feature-schema-v1";

    [Fact]
    public void AValidatedArtifactDrivesTheForecast()
    {
        Assert.True(HarVarianceModel.TryLoad(
            Artifact(), RuntimeHash, out HarVarianceModel model, out FittedModelRejection rejection));

        Assert.Equal(FittedModelRejection.None, rejection);
        Assert.True(model.IsFitted);

        // 0.001 + 0.4(2) + 0.3(3) + 0.2(4) = 2.501
        Assert.Equal(2.501d, model.Predict(2d, 3d, 4d)!.Value, precision: 9);
    }

    [Fact]
    public void AFeatureSchemaMismatchIsFatalAndNeverReordered()
    {
        // Section 4.1 makes this a hard model-invalid condition and prohibits best-effort
        // reordering, which is a strong rule about a weak failure: a model fed features in an order
        // it was not fitted on does not throw and does not look wrong. It produces confident numbers
        // from coefficients matched to the wrong inputs, and nothing downstream can tell.
        Assert.False(HarVarianceModel.TryLoad(
            Artifact() with { FeatureSchemaHash = "something-else" },
            RuntimeHash, out HarVarianceModel model, out FittedModelRejection rejection));

        Assert.Equal(FittedModelRejection.FeatureSchemaMismatch, rejection);
        Assert.False(model.IsFitted);
        Assert.Null(model.Predict(2d, 3d, 4d));
    }

    [Fact]
    public void AModelTypeWithNoInferencePathIsRefused()
    {
        // Better to refuse an HMM than to pretend a dot product is one.
        Assert.False(HarVarianceModel.TryLoad(
            Artifact() with { ModelType = "hmm" },
            RuntimeHash, out _, out FittedModelRejection rejection));

        Assert.Equal(FittedModelRejection.UnsupportedModelType, rejection);
    }

    [Fact]
    public void AnIncompleteManifestIsRefused()
    {
        // Every field exists so a live decision can be traced back to the fit that justified it.
        // The 26 registered strategy figures have none of this, and when the cost assumption behind
        // them turned out to be wrong there was no way to tell which conclusions depended on it.
        Assert.False(HarVarianceModel.TryLoad(
            Artifact() with { GitCommit = "" },
            RuntimeHash, out _, out FittedModelRejection rejection));

        Assert.Equal(FittedModelRejection.IncompleteManifest, rejection);
    }

    [Fact]
    public void AnUnpromotedArtifactCannotInformADecision()
    {
        // An experimental artifact is a record of work, not a licence to decide with it.
        Assert.False(HarVarianceModel.TryLoad(
            Artifact() with { PromotionState = "EXPERIMENTAL" },
            RuntimeHash, out _, out FittedModelRejection rejection));

        Assert.Equal(FittedModelRejection.InsufficientPromotion, rejection);
    }

    [Fact]
    public void CoefficientsAreReadByNameAndNeverByPosition()
    {
        // A parameters map that happens to enumerate in the right order today is not a contract,
        // and reading it positionally would reintroduce the ordering failure the schema hash exists
        // to prevent.
        FittedModelContract renamed = Artifact() with
        {
            Parameters = new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["intercept"] = 0.001d,
                ["beta_short"] = 0.4d,
                ["beta_medium"] = 0.3d,
                ["beta_wrong_name"] = 0.2d,
            },
        };

        Assert.False(HarVarianceModel.TryLoad(
            renamed, RuntimeHash, out _, out FittedModelRejection rejection));
        Assert.Equal(FittedModelRejection.UnusableParameters, rejection);
    }

    [Fact]
    public void ANonFiniteCoefficientIsRefused()
    {
        FittedModelContract broken = Artifact() with
        {
            Parameters = new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["intercept"] = double.NaN,
                ["beta_short"] = 0.4d,
                ["beta_medium"] = 0.3d,
                ["beta_long"] = 0.2d,
            },
        };

        Assert.False(HarVarianceModel.TryLoad(
            broken, RuntimeHash, out _, out FittedModelRejection rejection));
        Assert.Equal(FittedModelRejection.UnusableParameters, rejection);
    }

    [Fact]
    public void ANegativeFittedVarianceIsClampedRatherThanRefused()
    {
        // Least squares does not know a variance cannot be negative. The model saying "as close to
        // zero as I can express" is information, and an intercept going slightly negative on a
        // quiet window is ordinary rather than a fault.
        FittedModelContract negative = Artifact() with
        {
            Parameters = new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["intercept"] = -1d,
                ["beta_short"] = 0d,
                ["beta_medium"] = 0d,
                ["beta_long"] = 0d,
            },
        };

        Assert.True(HarVarianceModel.TryLoad(negative, RuntimeHash, out HarVarianceModel model, out _));
        Assert.Equal(0d, model.Predict(1d, 1d, 1d)!.Value, precision: 9);
    }

    [Fact]
    public void AnUnfittedModelForecastsNothingRatherThanSomethingPlausible()
    {
        // Null rather than a fallback, so the caller decides what to do about an absent model
        // instead of receiving an unfitted number that looks fitted.
        Assert.Null(HarVarianceModel.Unfitted().Predict(2d, 3d, 4d));
        Assert.False(HarVarianceModel.Unfitted().IsFitted);
    }

    [Fact]
    public void TheVolatilityExpertFallsBackToConventionalWeightsAndSaysWhichItUsed()
    {
        // Without an artifact nothing changes and nothing pretends otherwise: the expert keeps the
        // conventional weights it has always used and reports itself unfitted.
        Assert.False(new RealizedVolatilityExpert().IsFitted);

        HarVarianceModel.TryLoad(Artifact(), RuntimeHash, out HarVarianceModel model, out _);
        Assert.True(new RealizedVolatilityExpert(model).IsFitted);
    }

    private static FittedModelContract Artifact() => new(
        ArtifactId: "har-2026-09-02",
        ModelId: "crypto-realized-variance",
        ModelType: "har",
        ModelVersion: "1.0.0",
        FeatureSchemaHash: RuntimeHash,
        DatasetHash: "dataset-abc",
        Parameters: new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["intercept"] = 0.001d,
            ["beta_short"] = 0.4d,
            ["beta_medium"] = 0.3d,
            ["beta_long"] = 0.2d,
        },
        RandomSeed: 20260902,
        EvidenceGrade: "B",
        PromotionState: "VALIDATED",
        GitCommit: "abc1234",
        CreatedAt: DateTimeOffset.Parse("2026-09-02T00:00:00Z"));
}
