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
        var artifact = PythonResearchContractReader.ReadModelArtifact("""
            {"artifact_id":"artifact-1","model_id":"directional","model_version":"2","strategy_family":"regime_ensemble","feature_schema_hash":"schema-1",
             "artifact_hash":"artifact-hash","evidence_grade":"A","evidence_profile":{"evidence_id":"e","economic_hypothesis":"h","counter_hypothesis":"c","primary_evidence_ids":["source"],"transfer_grade":"A_Direct","transfer_reason":"direct"},"validation_gates":["R0","R1","R2","R3","R4","R5","R6","R7","R11","R12"],"support_domain":{"asset_class":"equity"},
             "creation_timestamp":"2026-08-29T12:00:00Z"}
            """);
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
        var artifact = PythonResearchContractReader.ReadModelArtifact("""
            {"artifact_id":"artifact-1","model_id":"directional","model_version":"2","strategy_family":"regime_ensemble","feature_schema_hash":"schema-1",
             "artifact_hash":"artifact-hash","evidence_grade":"A","evidence_profile":{"evidence_id":"e","economic_hypothesis":"h","counter_hypothesis":"c","primary_evidence_ids":["source"],"transfer_grade":"A_Direct","transfer_reason":"direct"},"validation_gates":["R0","R1","R2","R3","R4","R5","R6","R7","R11","R12"],"support_domain":{},"creation_timestamp":"2026-08-29T12:00:00Z"}
            """);
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
        var artifact = PythonResearchContractReader.ReadModelArtifact("""
            {"artifact_id":"artifact-1","model_id":"directional","model_version":"2","strategy_family":"regime_ensemble","feature_schema_hash":"schema-1",
             "artifact_hash":"artifact-hash","evidence_grade":"C_Weak","evidence_profile":{"evidence_id":"e","economic_hypothesis":"h","counter_hypothesis":"c","primary_evidence_ids":["source"],"transfer_grade":"C_Weak","transfer_reason":"weak"},"validation_gates":["R0","R1","R2","R3","R4","R5","R6","R7","R11","R12"],"support_domain":{},"creation_timestamp":"2026-08-29T12:00:00Z"}
            """);
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
        Assert.Throws<InvalidDataException>(() => PythonResearchContractReader.ReadModelArtifact("""
            {"artifact_id":"artifact-1","model_id":"directional","model_version":"2","strategy_family":"current-candle-winner","feature_schema_hash":"schema-1",
             "artifact_hash":"artifact-hash","evidence_grade":"A","evidence_profile":{"evidence_id":"e","economic_hypothesis":"h","counter_hypothesis":"c","primary_evidence_ids":["source"],"transfer_grade":"A_Direct","transfer_reason":"direct"},"validation_gates":["R0","R1","R2","R3","R4","R5","R6","R7","R11","R12"],"support_domain":{},"creation_timestamp":"2026-08-29T12:00:00Z"}
            """));
    }
}
