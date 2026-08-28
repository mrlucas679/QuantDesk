using QuantDesk.Runtime.Features;

namespace QuantDesk.Runtime.Tests.Features;

public sealed class FeatureSnapshotTests
{
    [Fact]
    public void CopiesAndValidatesFeatureVector()
    {
        string[] names = ["return_1m", "vol_20"];
        double[] values = [0.01, 0.2];
        FeatureSnapshot snapshot = FeatureSnapshotBuilder.Create(1, 4, 10, names, values, "schema-v1");
        names[0] = "mutated";
        values[0] = double.NaN;
        Assert.True(snapshot.IsComplete);
        Assert.Equal("return_1m", snapshot.FeatureNames[0]);
        Assert.Equal(0.01, snapshot.Values[0]);
    }

    [Fact]
    public void RejectsMismatchedOrNonFiniteVector()
    {
        Assert.Throws<ArgumentException>(() => FeatureSnapshotBuilder.Create(1, 1, 1, ["x"], [double.NaN], "schema"));
        Assert.Throws<ArgumentException>(() => FeatureSnapshotBuilder.Create(1, 1, 1, ["x"], [], "schema"));
    }
}
