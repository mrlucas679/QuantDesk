using System.Text.Json;
using QuantDesk.Domain.Contracts;

namespace QuantDesk.Domain.Tests.Contracts;

public sealed class RealisedCostContractTests
{
    /// <summary>Web defaults, matching what the API returns on the wire.</summary>
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    [Fact]
    public void TheCheckedInFixtureIsExactlyWhatThisTypeSerialisesTo()
    {
        // The pin. The research plane reads this file's shape in Python; if C# renames a field or
        // changes its casing, the two planes stop agreeing about what trading costs -- which is the
        // precise failure this whole contract exists to end. Failing here is far cheaper than
        // discovering it when a campaign silently charges the wrong number.
        RealisedCostContract contract = Deserialise();

        using JsonDocument expected = JsonDocument.Parse(File.ReadAllText(FixturePath));
        using JsonDocument actual = JsonDocument.Parse(JsonSerializer.Serialize(contract, Wire));

        Assert.Equal(
            JsonSerializer.Serialize(expected, Wire),
            JsonSerializer.Serialize(actual, Wire));
    }

    [Fact]
    public void TheFixtureIsAValidDataset()
    {
        Assert.True(Deserialise().IsValid());
    }

    [Fact]
    public void SizeSelectsTheBucketAndAnUnmeasuredSizeSelectsNone()
    {
        RealisedCostContract contract = Deserialise();

        Assert.Equal(71.2m, contract.UpperConfidenceCostBpsFor(10m));
        Assert.Equal(74.3m, contract.UpperConfidenceCostBpsFor(50m));
        Assert.Null(contract.UpperConfidenceCostBpsFor(500m));
        Assert.Equal(7, contract.ObservationCount);
    }

    [Fact]
    public void ABucketWhoseCountDisagreesWithItsSourcesIsInvalid()
    {
        // Provenance is not decoration. A count that does not match the listed trips means the
        // number cannot be traced to the fills behind it, and an untraceable cost is the thing this
        // type replaced.
        var lying = new RealisedCostBucket(0m, 25m, 9, 64m, 66m, 71.2m, ["only-one"]);

        Assert.False(lying.IsValid());
    }

    [Fact]
    public void ABoundBelowTheMeanIsInvalid()
    {
        Assert.False(new RealisedCostBucket(0m, 25m, 3, 64m, 66m, 60m, ["a", "b", "c"]).IsValid());
    }

    /// <summary>Walks up from the test binary to the repository root, as the other fixture tests do.</summary>
    private static string FixturePath
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, ".git")))
                directory = directory.Parent;
            return Path.Combine(
                directory?.FullName ?? AppContext.BaseDirectory,
                "tests", "fixtures", "research-contracts", "realised-costs.json");
        }
    }

    private static RealisedCostContract Deserialise()
    {
        RealisedCostContract? contract =
            JsonSerializer.Deserialize<RealisedCostContract>(File.ReadAllText(FixturePath), Wire);
        Assert.NotNull(contract);
        return contract;
    }
}
