namespace QuantDesk.Domain.Contracts;

public sealed record FeatureSchemaContract(
    string SchemaVersion,
    IReadOnlyList<string> FeatureNames,
    string FeatureHash)
{
    public bool IsValid() => !string.IsNullOrWhiteSpace(SchemaVersion)
        && !string.IsNullOrWhiteSpace(FeatureHash)
        && FeatureNames.Count > 0
        && FeatureNames.All(name => !string.IsNullOrWhiteSpace(name))
        && FeatureNames.Distinct(StringComparer.Ordinal).Count() == FeatureNames.Count;
}

public sealed record ModelArtifactContract(
    string ArtifactId,
    string ModelId,
    string ModelVersion,
    string FeatureSchemaHash,
    string ArtifactHash,
    string EvidenceGrade,
    string SupportDomain,
    DateTimeOffset CreationTimestamp)
{
    public bool IsValid() => !string.IsNullOrWhiteSpace(ArtifactId)
        && !string.IsNullOrWhiteSpace(ModelId)
        && !string.IsNullOrWhiteSpace(ModelVersion)
        && !string.IsNullOrWhiteSpace(FeatureSchemaHash)
        && !string.IsNullOrWhiteSpace(ArtifactHash)
        && !string.IsNullOrWhiteSpace(EvidenceGrade)
        && !string.IsNullOrWhiteSpace(SupportDomain);
}

public sealed record ForecastSnapshotContract(
    string ExpertId,
    string ModelId,
    string ModelVersion,
    string Instrument,
    DateTimeOffset AsOfTime,
    string ForecastFamily,
    int HorizonMinutes,
    decimal PointForecast,
    string FeatureSchemaHash,
    string ArtifactHash,
    string Status,
    string? ReasonCode)
{
    public bool IsValid() => !string.IsNullOrWhiteSpace(ExpertId)
        && !string.IsNullOrWhiteSpace(ModelId)
        && !string.IsNullOrWhiteSpace(ModelVersion)
        && !string.IsNullOrWhiteSpace(Instrument)
        && HorizonMinutes > 0
        && !string.IsNullOrWhiteSpace(ForecastFamily)
        && !string.IsNullOrWhiteSpace(FeatureSchemaHash)
        && !string.IsNullOrWhiteSpace(ArtifactHash)
        && !string.IsNullOrWhiteSpace(Status)
        && (string.Equals(Status, "valid", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(ReasonCode));
}

public static class ResearchContractValidator
{
    public static bool HasExactFeatureColumns(
        IReadOnlyList<string> expected,
        IReadOnlyList<string> actual)
        => expected.Count == actual.Count && expected.SequenceEqual(actual, StringComparer.Ordinal);
}
