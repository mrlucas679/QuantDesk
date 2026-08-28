namespace QuantDesk.Runtime.Features;

public sealed record FeatureSnapshot(
    int InstrumentSlot,
    long SourceStateVersion,
    long AsOfEventUnixNanoseconds,
    IReadOnlyList<string> FeatureNames,
    IReadOnlyList<double> Values,
    string SchemaHash)
{
    public bool IsComplete => FeatureNames.Count > 0
        && FeatureNames.Count == Values.Count
        && Values.All(double.IsFinite)
        && !string.IsNullOrWhiteSpace(SchemaHash);
}

public static class FeatureSnapshotBuilder
{
    public static FeatureSnapshot Create(
        int instrumentSlot,
        long sourceStateVersion,
        long asOfEventUnixNanoseconds,
        IReadOnlyList<string> featureNames,
        IReadOnlyList<double> values,
        string schemaHash)
    {
        ArgumentNullException.ThrowIfNull(featureNames);
        ArgumentNullException.ThrowIfNull(values);
        if (instrumentSlot < 0 || sourceStateVersion < 0 || asOfEventUnixNanoseconds < 0 ||
            featureNames.Count == 0 || featureNames.Count != values.Count ||
            featureNames.Any(string.IsNullOrWhiteSpace) || values.Any(value => !double.IsFinite(value)) ||
            string.IsNullOrWhiteSpace(schemaHash))
            throw new ArgumentException("Feature snapshot is incomplete or non-deterministic.");
        return new FeatureSnapshot(instrumentSlot, sourceStateVersion, asOfEventUnixNanoseconds,
            featureNames.ToArray(), values.ToArray(), schemaHash.Trim());
    }
}
