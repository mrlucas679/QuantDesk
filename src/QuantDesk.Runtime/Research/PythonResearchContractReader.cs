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
        JsonElement evidence = RequireProperty(root, "evidence_profile");
        JsonElement validationEvidence = RequireProperty(root, "validation_evidence");
        if (supportDomain.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Model artifact support_domain must be an object.");
        if (validationEvidence.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Model artifact validation_evidence must be an object.");
        var contract = new ModelArtifactContract(
            RequirePropertyString(root, "artifact_id"),
            RequirePropertyString(root, "model_id"),
            RequirePropertyString(root, "model_version"),
            RequirePropertyString(root, "strategy_family"),
            ReadStrategyDefinition(RequireProperty(root, "strategy_definition")),
            RequirePropertyString(root, "feature_schema_hash"),
            RequirePropertyString(root, "artifact_hash"),
            RequirePropertyString(root, "evidence_grade"),
            ReadEvidenceProfile(evidence),
            RequireArray(root, "validation_gates").EnumerateArray()
                .Select(value => RequireString(value, "validation_gates[]")).ToArray(),
            ReadValidationEvidence(validationEvidence),
            supportDomain.GetRawText(),
            RequireTimestamp(root, "creation_timestamp"));
        return contract.IsValid() ? contract : throw new InvalidDataException("Model artifact contract is invalid.");
    }

    private static StrategyDefinitionContract ReadStrategyDefinition(JsonElement definition)
    {
        JsonElement root = RequireObject(definition);
        JsonElement exit = RequireObject(RequireProperty(root, "exit_policy"));
        var contract = new StrategyDefinitionContract(
            RequirePropertyString(root, "symbol"),
            RequirePositiveInt(root, "bar_duration_minutes"),
            RequirePositiveInt(root, "forecast_horizon_minutes"),
            RequirePropertyString(root, "entry_rule_version"),
            RequirePropertyString(root, "signal_type"),
            RequireObject(RequireProperty(root, "parameters")).GetRawText(),
            new ExitPolicyDefinitionContract(
                RequirePropertyString(exit, "policy_version"),
                RequirePositiveInt(exit, "maximum_holding_minutes"),
                RequireBoolean(exit, "exit_on_thesis_invalidation"),
                RequireBoolean(exit, "exit_on_regime_change")));
        string executionKind = OptionalString(root, "execution_kind") ?? "spot";
        return executionKind switch
        {
            "spot" => contract,
            "defined_risk_vertical" => contract with
            {
                ExecutionKind = StrategyExecutionKind.DefinedRiskVertical,
                OptionVertical = ReadOptionVerticalPolicy(RequireObject(RequireProperty(root, "option_vertical")))
            },
            _ => throw new InvalidDataException("Strategy definition has an unsupported execution_kind.")
        };
    }

    private static OptionVerticalExecutionPolicyContract ReadOptionVerticalPolicy(JsonElement policy) => new(
        RequirePositiveInt(policy, "minimum_days_to_expiry"),
        RequirePositiveInt(policy, "maximum_days_to_expiry"),
        RequirePositiveDecimal(policy, "strike_band_fraction"),
        RequirePositiveDecimal(policy, "maximum_defined_loss"),
        RequirePositiveDecimal(policy, "exit_limit_fraction"));

    private static IReadOnlyDictionary<string, ValidationGateEvidenceContract> ReadValidationEvidence(
        JsonElement evidence)
    {
        var results = new Dictionary<string, ValidationGateEvidenceContract>(StringComparer.Ordinal);
        foreach (JsonProperty property in evidence.EnumerateObject())
        {
            JsonElement value = RequireObject(property.Value);
            var result = new ValidationGateEvidenceContract(
                RequirePropertyString(value, "gate_id"),
                RequireBoolean(value, "passed"),
                RequireArray(value, "evidence_ids").EnumerateArray()
                    .Select(item => RequireString(item, "evidence_ids[]")).ToArray(),
                RequireTimestamp(value, "evaluated_at"),
                RequireObject(RequireProperty(value, "details")).GetRawText());
            if (!results.TryAdd(property.Name, result))
                throw new InvalidDataException($"Duplicate validation evidence for '{property.Name}'.");
        }
        return results;
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
            !string.Equals(artifact.StrategyDefinition.Symbol, forecast.Instrument, StringComparison.OrdinalIgnoreCase) ||
            artifact.StrategyDefinition.ForecastHorizonMinutes != forecast.HorizonMinutes ||
            !string.Equals(artifact.FeatureSchemaHash, schema.FeatureHash, StringComparison.Ordinal) ||
            !string.Equals(forecast.FeatureSchemaHash, schema.FeatureHash, StringComparison.Ordinal) ||
            !string.Equals(forecast.ArtifactHash, artifact.ArtifactHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Forecast does not match its approved model artifact and feature schema.");
        }
        if (!artifact.EvidenceProfile.IsExecutionEligible())
            throw new InvalidDataException("Model artifact evidence transfer is not execution-eligible.");
        if (!artifact.HasRequiredExecutionGates())
            throw new InvalidDataException("Model artifact is missing required execution validation gates.");
    }

    private static EvidenceProfileContract ReadEvidenceProfile(JsonElement evidence)
    {
        JsonElement root = RequireObject(evidence);
        string[] primaryIds = RequireArray(root, "primary_evidence_ids")
            .EnumerateArray().Select(value => RequireString(value, "primary_evidence_ids[]")).ToArray();
        var profile = new EvidenceProfileContract(
            RequirePropertyString(root, "evidence_id"),
            RequirePropertyString(root, "economic_hypothesis"),
            RequirePropertyString(root, "counter_hypothesis"),
            primaryIds,
            RequirePropertyString(root, "transfer_grade"),
            RequirePropertyString(root, "transfer_reason"));
        return profile.IsValid() ? profile : throw new InvalidDataException("Artifact evidence profile is invalid.");
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

    private static decimal RequirePositiveDecimal(JsonElement root, string name) =>
        RequireProperty(root, name).TryGetDecimal(out decimal value) && value > 0
            ? value : throw new InvalidDataException($"Research contract '{name}' must be a positive decimal.");

    private static decimal RequireDecimal(JsonElement root, string name) => RequireProperty(root, name).TryGetDecimal(out decimal value)
        ? value : throw new InvalidDataException($"Research contract '{name}' must be a finite number.");

    private static bool RequireBoolean(JsonElement root, string name)
    {
        JsonElement value = RequireProperty(root, name);
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException($"Research contract '{name}' must be a boolean.")
        };
    }
}
