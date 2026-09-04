using System.Text.Json.Nodes;
using QuantDesk.Domain.Contracts;
using QuantDesk.Runtime.Research;

namespace QuantDesk.Runtime.Tests.Research;

public sealed class PythonResearchContractReaderTests
{
    [Fact]
    public void PythonArtifactSchemaAndForecast_ValidateWhenHashesMatch()
    {
        var schema = PythonResearchContractReader.ReadFeatureSchema("""
            {"schema_version":"1","feature_names":["return_1m","vol_5m"],"feature_hash":"schema-1"}
            """);
        var artifact = PythonResearchContractReader.ReadModelArtifact(WithValidationEvidence("""
            {"artifact_id":"artifact-1","model_id":"directional","model_version":"2","strategy_family":"regime_ensemble","feature_schema_hash":"schema-1",
             "artifact_hash":"artifact-hash","evidence_grade":"A","evidence_profile":{"evidence_id":"e","economic_hypothesis":"h","counter_hypothesis":"c","primary_evidence_ids":["source"],"transfer_grade":"A_Direct","transfer_reason":"direct"},"validation_gates":["R0","R1","R2","R3","R4","R5","R6","R7","R11","R12"],"support_domain":{"asset_class":"equity"},
             "creation_timestamp":"2026-08-29T12:00:00Z"}
            """));
        var forecast = PythonResearchContractReader.ReadForecast("""
            {"expert_id":"expert-1","model_id":"directional","model_version":"2","instrument":"SPY",
             "as_of_time":"2026-08-29T12:01:00Z","forecast_family":"directional","horizon_minutes":5,
             "point_forecast":0.12,"feature_schema_hash":"schema-1","artifact_hash":"artifact-hash",
             "status":"valid","reason_code":null}
            """);

        PythonResearchContractReader.ValidateForecast(artifact, schema, forecast);
    }

    [Fact]
    public void ValidateForecast_RejectsArtifactHashMismatch()
    {
        var schema = PythonResearchContractReader.ReadFeatureSchema("""
            {"schema_version":"1","feature_names":["return_1m"],"feature_hash":"schema-1"}
            """);
        var artifact = PythonResearchContractReader.ReadModelArtifact(WithValidationEvidence("""
            {"artifact_id":"artifact-1","model_id":"directional","model_version":"2","strategy_family":"regime_ensemble","feature_schema_hash":"schema-1",
             "artifact_hash":"artifact-hash","evidence_grade":"A","evidence_profile":{"evidence_id":"e","economic_hypothesis":"h","counter_hypothesis":"c","primary_evidence_ids":["source"],"transfer_grade":"A_Direct","transfer_reason":"direct"},"validation_gates":["R0","R1","R2","R3","R4","R5","R6","R7","R11","R12"],"support_domain":{},"creation_timestamp":"2026-08-29T12:00:00Z"}
            """));
        var forecast = PythonResearchContractReader.ReadForecast("""
            {"expert_id":"expert-1","model_id":"directional","model_version":"2","instrument":"SPY",
             "as_of_time":"2026-08-29T12:01:00Z","forecast_family":"directional","horizon_minutes":5,
             "point_forecast":0.12,"feature_schema_hash":"schema-1","artifact_hash":"wrong","status":"valid"}
            """);

        Assert.Throws<InvalidDataException>(() => PythonResearchContractReader.ValidateForecast(artifact, schema, forecast));
    }

    [Fact]
    public void Reader_RejectsPythonInvalidStatusWithoutReason()
    {
        Assert.Throws<InvalidDataException>(() => PythonResearchContractReader.ReadForecast("""
            {"expert_id":"expert-1","model_id":"directional","model_version":"2","instrument":"SPY",
             "as_of_time":"2026-08-29T12:01:00Z","forecast_family":"directional","horizon_minutes":5,
             "point_forecast":0.12,"feature_schema_hash":"schema-1","artifact_hash":"artifact-hash","status":"failed"}
            """));
    }

    [Fact]
    public void ValidateForecast_RejectsWeakEvidenceTransfer()
    {
        var schema = PythonResearchContractReader.ReadFeatureSchema("""
            {"schema_version":"1","feature_names":["return_1m"],"feature_hash":"schema-1"}
            """);
        var artifact = PythonResearchContractReader.ReadModelArtifact(WithValidationEvidence("""
            {"artifact_id":"artifact-1","model_id":"directional","model_version":"2","strategy_family":"regime_ensemble","feature_schema_hash":"schema-1",
             "artifact_hash":"artifact-hash","evidence_grade":"C_Weak","evidence_profile":{"evidence_id":"e","economic_hypothesis":"h","counter_hypothesis":"c","primary_evidence_ids":["source"],"transfer_grade":"C_Weak","transfer_reason":"weak"},"validation_gates":["R0","R1","R2","R3","R4","R5","R6","R7","R11","R12"],"support_domain":{},"creation_timestamp":"2026-08-29T12:00:00Z"}
            """));
        var forecast = PythonResearchContractReader.ReadForecast("""
            {"expert_id":"expert-1","model_id":"directional","model_version":"2","instrument":"BTC/USD",
             "as_of_time":"2026-08-29T12:01:00Z","forecast_family":"directional_return_bps","horizon_minutes":5,
             "point_forecast":0.12,"feature_schema_hash":"schema-1","artifact_hash":"artifact-hash","status":"valid"}
            """);

        Assert.Throws<InvalidDataException>(() => PythonResearchContractReader.ValidateForecast(artifact, schema, forecast));
    }

    [Fact]
    public void Reader_RejectsUnknownStrategyFamily()
    {
        Assert.Throws<InvalidDataException>(() => PythonResearchContractReader.ReadModelArtifact(WithValidationEvidence("""
            {"artifact_id":"artifact-1","model_id":"directional","model_version":"2","strategy_family":"current-candle-winner","feature_schema_hash":"schema-1",
             "artifact_hash":"artifact-hash","evidence_grade":"A","evidence_profile":{"evidence_id":"e","economic_hypothesis":"h","counter_hypothesis":"c","primary_evidence_ids":["source"],"transfer_grade":"A_Direct","transfer_reason":"direct"},"validation_gates":["R0","R1","R2","R3","R4","R5","R6","R7","R11","R12"],"support_domain":{},"creation_timestamp":"2026-08-29T12:00:00Z"}
            """)));
    }

    [Fact]
    public void Reader_RejectsGateNamesWithoutValidationEvidence()
    {
        Assert.Throws<InvalidDataException>(() => PythonResearchContractReader.ReadModelArtifact("""
            {"artifact_id":"artifact-1","model_id":"directional","model_version":"2","strategy_family":"regime_ensemble","feature_schema_hash":"schema-1",
             "artifact_hash":"artifact-hash","evidence_grade":"A","evidence_profile":{"evidence_id":"e","economic_hypothesis":"h","counter_hypothesis":"c","primary_evidence_ids":["source"],"transfer_grade":"A_Direct","transfer_reason":"direct"},"validation_gates":["R0","R1","R2","R3","R4","R5","R6","R7","R11","R12"],"support_domain":{},"creation_timestamp":"2026-08-29T12:00:00Z"}
            """));
    }

    [Fact]
    public void Reader_BindsDefinedRiskVerticalPolicyToTheArtifact()
    {
        JsonObject root = JsonNode.Parse(WithValidationEvidence("""
            {"artifact_id":"artifact-1","model_id":"directional","model_version":"2","strategy_family":"regime_ensemble","feature_schema_hash":"schema-1",
             "artifact_hash":"artifact-hash","evidence_grade":"A","evidence_profile":{"evidence_id":"e","economic_hypothesis":"h","counter_hypothesis":"c","primary_evidence_ids":["source"],"transfer_grade":"A_Direct","transfer_reason":"direct"},"validation_gates":["R0","R1","R2","R3","R4","R5","R6","R7","R11","R12"],"support_domain":{},"creation_timestamp":"2026-08-29T12:00:00Z"}
            """))!.AsObject();
        JsonObject definition = root["strategy_definition"]!.AsObject();
        definition["execution_kind"] = "defined_risk_vertical";
        definition["option_vertical"] = new JsonObject
        {
            ["minimum_days_to_expiry"] = 7,
            ["maximum_days_to_expiry"] = 60,
            ["strike_band_fraction"] = 0.05,
            ["maximum_defined_loss"] = 20,
            ["exit_limit_fraction"] = 0.5
        };

        ModelArtifactContract artifact = PythonResearchContractReader.ReadModelArtifact(root.ToJsonString());

        Assert.Equal(StrategyExecutionKind.DefinedRiskVertical, artifact.StrategyDefinition.ExecutionKind);
        Assert.Equal(20m, artifact.StrategyDefinition.OptionVertical!.MaximumDefinedLoss);
    }

    private static string WithValidationEvidence(string json)
    {
        JsonObject root = JsonNode.Parse(json)?.AsObject()
            ?? throw new InvalidOperationException("Test artifact JSON is invalid.");
        root["strategy_definition"] = new JsonObject
        {
            ["symbol"] = "SPY",
            ["bar_duration_minutes"] = 5,
            ["forecast_horizon_minutes"] = 5,
            ["entry_rule_version"] = "trend-state-v1",
            ["signal_type"] = "State",
            ["parameters"] = new JsonObject { ["minimum_expected_return_bps"] = 1 },
            ["exit_policy"] = new JsonObject
            {
                ["policy_version"] = "managed-v1",
                ["maximum_holding_minutes"] = 60,
                ["exit_on_thesis_invalidation"] = true,
                ["exit_on_regime_change"] = true
            }
        };
        var evidence = new JsonObject();
        foreach (string gate in new[] { "R0", "R1", "R2", "R3", "R4", "R5", "R6", "R7", "R11", "R12" })
        {
            evidence[gate] = new JsonObject
            {
                ["gate_id"] = gate,
                ["passed"] = true,
                ["evidence_ids"] = new JsonArray($"evidence-{gate}"),
                ["evaluated_at"] = "2026-08-29T12:00:00Z",
                ["details"] = new JsonObject { ["test_fixture"] = true }
            };
        }
        root["validation_evidence"] = evidence;
        return root.ToJsonString();
    }
}
