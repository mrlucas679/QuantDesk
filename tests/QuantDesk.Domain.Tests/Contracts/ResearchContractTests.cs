using QuantDesk.Domain.Contracts;

namespace QuantDesk.Domain.Tests.Contracts;

public sealed class ResearchContractTests
{
    [Fact]
    public void FeatureColumnsRequireExactOrderAndMembership()
    {
        string[] expected = ["r_1m", "r_5m", "vol_20"];
        Assert.True(ResearchContractValidator.HasExactFeatureColumns(expected, ["r_1m", "r_5m", "vol_20"]));
        Assert.False(ResearchContractValidator.HasExactFeatureColumns(expected, ["r_5m", "r_1m", "vol_20"]));
        Assert.False(ResearchContractValidator.HasExactFeatureColumns(expected, ["r_1m", "r_5m"]));
    }

    [Fact]
    public void ArtifactAndForecastRequireHashes()
    {
        var artifact = new ModelArtifactContract("a", "m", "1", "price_volume_directional", "schema", "artifact", "B",
            new EvidenceProfileContract("e", "hypothesis", "counter", ["source"], "B_Close", "close domain"),
            ["R0", "R1", "R2", "R3", "R4", "R5", "R6", "R7", "R11", "R12"], "equity", DateTimeOffset.UtcNow);
        var forecast = new ForecastSnapshotContract("e", "m", "1", "AAPL", DateTimeOffset.UtcNow, "directional", 5, 1m, "schema", "artifact", "valid", "ok");
        Assert.True(artifact.IsValid());
        Assert.True(forecast.IsValid());
        Assert.False((forecast with { ArtifactHash = "" }).IsValid());
    }
}
