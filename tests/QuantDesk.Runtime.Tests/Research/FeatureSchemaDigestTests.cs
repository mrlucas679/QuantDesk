using QuantDesk.Runtime.Experts;
using QuantDesk.Runtime.Research;

namespace QuantDesk.Runtime.Tests.Research;

/// <summary>
/// The runtime derives the schema hash it expects, and it has to be the one Python computes.
///
/// Why this matters more than it looks
/// -----------------------------------
/// Until now the schema hash lived only inside the artifact, and the loader compared it to itself.
/// That passes for every artifact, including one fitted on a different feature set -- which is the
/// exact failure the hash was introduced to prevent, and section 4.1 calls fatal.
///
/// Deriving it here means the check can fail. It also means a recipe reimplemented from Python can
/// drift, so these pin it against the committed fixtures. A drift refuses every model rather than
/// accepting a wrong one -- the safe direction -- and this test says so before a model does.
/// </summary>
public sealed class FeatureSchemaDigestTests
{
    [Fact]
    public void TheRuntimeDerivesTheHashThatPythonWroteIntoTheHarFixture()
    {
        // The single assertion that makes the whole schema check real. If the two recipes agree on
        // this, the runtime can state what it computes and refuse anything else.
        string committed = FixtureSchemaHash("har-realised-variance.json");

        Assert.Equal(committed, RealizedVolatilityExpert.FeatureContract.FeatureSchemaHash);
    }

    [Fact]
    public void ChangingAnyPartOfTheSchemaChangesTheHash()
    {
        // A hash that ignored one of its inputs would pass the test above and protect nothing.
        string baseline = FeatureSchemaDigest.Compute(
            "har-realised-variance-v1", Names, Dtypes, 288, Sources);

        Assert.NotEqual(baseline, FeatureSchemaDigest.Compute(
            "har-realised-variance-v2", Names, Dtypes, 288, Sources));
        Assert.NotEqual(baseline, FeatureSchemaDigest.Compute(
            "har-realised-variance-v1", ["rv_short", "rv_long", "rv_medium"], Dtypes, 288, Sources));
        Assert.NotEqual(baseline, FeatureSchemaDigest.Compute(
            "har-realised-variance-v1", Names, Dtypes, 289, Sources));
        Assert.NotEqual(baseline, FeatureSchemaDigest.Compute(
            "har-realised-variance-v1", Names, Dtypes, 288, ["alpaca_ohlcv", "orderbook"]));
        Assert.NotEqual(baseline, FeatureSchemaDigest.Compute(
            "har-realised-variance-v1", Names,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["rv_short"] = "float32", ["rv_medium"] = "float64", ["rv_long"] = "float64",
            },
            288, Sources));
    }

    [Fact]
    public void FeatureOrderChangesTheHashEvenThoughTheSetIsIdentical()
    {
        // The reason ordering is in the hash at all. A model fed the same three features in a
        // different order produces confident numbers from coefficients matched to the wrong inputs,
        // and nothing downstream can tell.
        Assert.NotEqual(
            FeatureSchemaDigest.Compute("v1", ["a", "b"], TwoTypes, 10, Sources),
            FeatureSchemaDigest.Compute("v1", ["b", "a"], TwoTypes, 10, Sources));
    }

    [Fact]
    public void DtypeOrderDoesNotChangeTheHash()
    {
        // dtypes is a mapping on both sides and is sorted before hashing, so the order a .NET
        // dictionary happens to enumerate in must not reach the digest.
        Assert.Equal(
            FeatureSchemaDigest.Compute("v1", ["a", "b"], TwoTypes, 10, Sources),
            FeatureSchemaDigest.Compute("v1", ["a", "b"],
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["b"] = "float64", ["a"] = "float64",
                },
                10, Sources));
    }

    [Fact]
    public void ANonAsciiIdentifierIsRefusedRatherThanEncodedDifferently()
    {
        // Python escapes non-ASCII by default; this does not implement that, so it refuses instead
        // of quietly producing a hash the other side would never compute.
        Assert.Throws<InvalidDataException>(() =>
            FeatureSchemaDigest.Compute("v1", ["rv_shört"], TwoTypes, 10, Sources));
    }

    [Fact]
    public void TheRuntimeContractStatesUnitsTheHashDoesNotCover()
    {
        // The hash covers names, dtypes, normalization, warm-up and sources. It does not cover what
        // the numbers mean, which is why the contract carries units separately.
        Assert.All(
            RealizedVolatilityExpert.FeatureContract.Units.Values,
            unit => Assert.Equal(RealizedVolatilityExpert.VarianceUnits, unit));
        Assert.Equal(5, RealizedVolatilityExpert.FeatureContract.BarDurationMinutes);
        Assert.True(RealizedVolatilityExpert.FeatureContract.DeclaresSemantics);
    }

    private static readonly string[] Names = ["rv_short", "rv_medium", "rv_long"];
    private static readonly string[] Sources = ["alpaca_ohlcv"];

    private static readonly Dictionary<string, string> Dtypes = new(StringComparer.Ordinal)
    {
        ["rv_short"] = "float64", ["rv_medium"] = "float64", ["rv_long"] = "float64",
    };

    private static readonly Dictionary<string, string> TwoTypes = new(StringComparer.Ordinal)
    {
        ["a"] = "float64", ["b"] = "float64",
    };

    private static string FixtureSchemaHash(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, ".git")))
            directory = directory.Parent;

        string path = Path.Combine(
            directory?.FullName ?? AppContext.BaseDirectory,
            "tests", "fixtures", "model-artifacts", name);

        using System.Text.Json.JsonDocument document =
            System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("feature_schema_hash").GetString()!;
    }
}
