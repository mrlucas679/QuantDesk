using System.Globalization;
using System.Text.Json;
using QuantDesk.Domain.Contracts;

namespace QuantDesk.Runtime.Research;

/// <summary>
/// Reads the public JSON contracts emitted by QuantDesk Research Python code.
/// The reader deliberately accepts only the Python contract's snake_case fields.
/// </summary>
public static class PythonResearchContractReader
{
    public static FeatureSchemaContract ReadFeatureSchema(string json)
    {
        using JsonDocument document = Parse(json);
        JsonElement root = RequireObject(document.RootElement);
        var names = RequireArray(root, "feature_names")
            .EnumerateArray().Select(value => RequireString(value, "feature_names[]")).ToArray();
        var contract = new FeatureSchemaContract(
            RequirePropertyString(root, "schema_version"), names, RequirePropertyString(root, "feature_hash"));
        return contract.IsValid() ? contract : throw new InvalidDataException("Feature schema contract is invalid.");
    }

    public static ModelArtifactContract ReadModelArtifact(string json)
    {
        using JsonDocument document = Parse(json);
        JsonElement root = RequireObject(document.RootElement);
        JsonElement supportDomain = RequireProperty(root, "support_domain");
        if (supportDomain.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Model artifact support_domain must be an object.");
        var contract = new ModelArtifactContract(
            RequirePropertyString(root, "artifact_id"),
            RequirePropertyString(root, "model_id"),
            RequirePropertyString(root, "model_version"),
            RequirePropertyString(root, "feature_schema_hash"),
            RequirePropertyString(root, "artifact_hash"),
            RequirePropertyString(root, "evidence_grade"),
            supportDomain.GetRawText(),
            RequireTimestamp(root, "creation_timestamp"));
        return contract.IsValid() ? contract : throw new InvalidDataException("Model artifact contract is invalid.");
    }

    public static ForecastSnapshotContract ReadForecast(string json)
    {
        using JsonDocument document = Parse(json);
        JsonElement root = RequireObject(document.RootElement);
        string status = RequirePropertyString(root, "status");
        string? reason = OptionalString(root, "reason_code");
        var contract = new ForecastSnapshotContract(
            RequirePropertyString(root, "expert_id"),
            RequirePropertyString(root, "model_id"),
            RequirePropertyString(root, "model_version"),
            RequirePropertyString(root, "instrument"),
            RequireTimestamp(root, "as_of_time"),
            RequirePropertyString(root, "forecast_family"),
            RequirePositiveInt(root, "horizon_minutes"),
            RequireDecimal(root, "point_forecast"),
            RequirePropertyString(root, "feature_schema_hash"),
            RequirePropertyString(root, "artifact_hash"),
            status,
            reason);
        return contract.IsValid() ? contract : throw new InvalidDataException("Forecast contract is invalid.");
    }

    public static void ValidateForecast(ModelArtifactContract artifact, FeatureSchemaContract schema, ForecastSnapshotContract forecast)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(forecast);
        if (!artifact.IsValid() || !schema.IsValid() || !forecast.IsValid() ||
            !string.Equals(artifact.ModelId, forecast.ModelId, StringComparison.Ordinal) ||
            !string.Equals(artifact.ModelVersion, forecast.ModelVersion, StringComparison.Ordinal) ||
            !string.Equals(artifact.FeatureSchemaHash, schema.FeatureHash, StringComparison.Ordinal) ||
            !string.Equals(forecast.FeatureSchemaHash, schema.FeatureHash, StringComparison.Ordinal) ||
            !string.Equals(forecast.ArtifactHash, artifact.ArtifactHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Forecast does not match its approved model artifact and feature schema.");
        }
    }

    private static JsonDocument Parse(string json)
    {
        try { return JsonDocument.Parse(json); }
        catch (JsonException exception) { throw new InvalidDataException("Research contract is not valid JSON.", exception); }
    }

    private static JsonElement RequireObject(JsonElement element) => element.ValueKind == JsonValueKind.Object
        ? element : throw new InvalidDataException("Research contract root must be an object.");

    private static JsonElement RequireProperty(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) ? value : throw new InvalidDataException($"Research contract is missing '{name}'.");

    private static JsonElement RequireArray(JsonElement root, string name)
    {
        JsonElement value = RequireProperty(root, name);
        return value.ValueKind == JsonValueKind.Array ? value : throw new InvalidDataException($"Research contract '{name}' must be an array.");
    }

    private static string RequirePropertyString(JsonElement root, string name) => RequireString(RequireProperty(root, name), name);

    private static string RequireString(JsonElement value, string name) => value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
        ? value.GetString()! : throw new InvalidDataException($"Research contract '{name}' must be a non-empty string.");

    private static string? OptionalString(JsonElement root, string name) => !root.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null
        ? null : RequireString(value, name);

    private static DateTimeOffset RequireTimestamp(JsonElement root, string name) => DateTimeOffset.TryParse(
        RequirePropertyString(root, name), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset value)
        ? value : throw new InvalidDataException($"Research contract '{name}' must be an ISO-8601 timestamp.");

    private static int RequirePositiveInt(JsonElement root, string name) => RequireProperty(root, name).TryGetInt32(out int value) && value > 0
        ? value : throw new InvalidDataException($"Research contract '{name}' must be a positive integer.");

    private static decimal RequireDecimal(JsonElement root, string name) => RequireProperty(root, name).TryGetDecimal(out decimal value)
        ? value : throw new InvalidDataException($"Research contract '{name}' must be a finite number.");
}
