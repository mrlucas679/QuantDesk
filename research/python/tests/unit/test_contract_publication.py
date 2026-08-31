from datetime import UTC, datetime
from pathlib import Path

import pytest

from quantdesk_research.contracts.feature_schema import FeatureSchema
from quantdesk_research.contracts.forecast import Forecast
from quantdesk_research.contracts.model_artifact import EvidenceProfile, ModelArtifact
from quantdesk_research.models.contract_publication import (
    REQUIRED_EXECUTION_GATES,
    ContractPublisher,
)
from quantdesk_research.models.model_registry import ModelRegistry


def _contracts() -> tuple[FeatureSchema, ModelArtifact, Forecast]:
    schema = FeatureSchema(
        schema_version="v1",
        feature_names=["return_1"],
        dtypes={"return_1": "float"},
        normalization={},
        lookback_periods=1,
        source_requirements=["bars"],
        feature_hash="schema-hash",
    )
    artifact = ModelArtifact(
        artifact_id="artifact-1",
        model_id="btc-model",
        model_type="lightgbm",
        model_version="v1",
        strategy_family="price_volume_directional",
        feature_schema_hash="schema-hash",
        dataset_hash="dataset-hash",
        training_window={},
        parameters={},
        random_seed=42,
        metrics={},
        evidence_grade="B_Close",
        evidence_profile=EvidenceProfile(
            evidence_id="evidence-1",
            economic_hypothesis="test hypothesis",
            counter_hypothesis="test counter-hypothesis",
            primary_evidence_ids=["source-1"],
            transfer_grade="B_Close",
            transfer_reason="test evidence",
        ),
        validation_gates=sorted(REQUIRED_EXECUTION_GATES),
        support_domain={},
        git_commit="test",
        config_hash="config",
        creation_timestamp=datetime.now(UTC),
        artifact_hash="artifact-hash",
    )
    forecast = Forecast(
        expert_id="btc-model",
        model_id="btc-model",
        model_version="v1",
        instrument="BTC/USD",
        as_of_time=datetime.now(UTC),
        forecast_family="directional_return_bps",
        horizon_minutes=5,
        point_forecast=12.0,
        confidence=0.8,
        calibration_status="validated",
        support_domain_status="in_domain",
        feature_schema_hash="schema-hash",
        artifact_hash="artifact-hash",
        status="valid",
    )
    return schema, artifact, forecast


def test_publishes_complete_bundle_before_marking_model_validated(tmp_path: Path):
    root = tmp_path / "artifacts"
    registry = ModelRegistry(str(tmp_path / "experiments.db"))
    schema, artifact, forecast = _contracts()

    ContractPublisher(root, registry).publish_validated(
        schema, artifact, forecast, root / "model.json"
    )

    assert (root / "current-contracts.json").exists()
    assert registry.list_models("VALIDATED")[0]["artifact_id"] == "artifact-1"


def test_rejects_mismatched_hash_without_publishing_pointer(tmp_path: Path):
    root = tmp_path / "artifacts"
    registry = ModelRegistry(str(tmp_path / "experiments.db"))
    schema, artifact, forecast = _contracts()
    forecast.artifact_hash = "other-hash"

    with pytest.raises(ValueError, match="artifact hash"):
        ContractPublisher(root, registry).publish_validated(
            schema, artifact, forecast, root / "model.json"
        )

    assert not (root / "current-contracts.json").exists()
    assert registry.list_models("VALIDATED") == []


def test_rejects_weak_evidence_without_publishing_pointer(tmp_path: Path):
    root = tmp_path / "artifacts"
    registry = ModelRegistry(str(tmp_path / "experiments.db"))
    schema, artifact, forecast = _contracts()
    artifact.evidence_profile.transfer_grade = "C_Weak"

    with pytest.raises(ValueError, match="execution-eligible"):
        ContractPublisher(root, registry).publish_validated(
            schema, artifact, forecast, root / "model.json"
        )

    assert not (root / "current-contracts.json").exists()


def test_rejects_incomplete_validation_gates_without_publishing_pointer(tmp_path: Path):
    root = tmp_path / "artifacts"
    registry = ModelRegistry(str(tmp_path / "experiments.db"))
    schema, artifact, forecast = _contracts()
    artifact.validation_gates.remove("R3")

    with pytest.raises(ValueError, match="missing required validation gates"):
        ContractPublisher(root, registry).publish_validated(
            schema, artifact, forecast, root / "model.json"
        )

    assert not (root / "current-contracts.json").exists()


def test_rejects_unknown_strategy_family() -> None:
    _, artifact, _ = _contracts()

    with pytest.raises(ValueError, match="strategy_family is not registered"):
        ModelArtifact(**(artifact.model_dump() | {"strategy_family": "current-candle-winner"}))
