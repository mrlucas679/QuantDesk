from datetime import UTC, datetime
from pathlib import Path

import pytest

from quantdesk_research.contracts.feature_schema import FeatureSchema
from quantdesk_research.contracts.forecast import Forecast, ForecastUncertainty
from quantdesk_research.contracts.model_artifact import (
    EvidenceProfile,
    ExitPolicyDefinition,
    ModelArtifact,
    OptionVerticalExecutionPolicy,
    StrategyDefinition,
    ValidationGateEvidence,
)
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
    evaluated_at = datetime.now(UTC)
    artifact = ModelArtifact(
        artifact_id="artifact-1",
        model_id="btc-model",
        model_type="lightgbm",
        model_version="v1",
        strategy_family="price_volume_directional",
        strategy_definition=StrategyDefinition(
            symbol="BTC/USD",
            bar_duration_minutes=5,
            forecast_horizon_minutes=5,
            entry_rule_version="price-volume-directional-v1",
            signal_type="State",
            parameters={"minimum_expected_return_bps": 1.0},
            exit_policy=ExitPolicyDefinition(
                policy_version="crypto-directional-exit-v1",
                maximum_holding_minutes=5,
                exit_on_thesis_invalidation=True,
                exit_on_regime_change=True,
            ),
        ),
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
        validation_evidence={
            gate_id: ValidationGateEvidence(
                gate_id=gate_id,
                passed=True,
                evidence_ids=[f"evidence-{gate_id}"],
                evaluated_at=evaluated_at,
                details={"test_fixture": True},
            )
            for gate_id in REQUIRED_EXECUTION_GATES
        },
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
        units="basis_points",
        # A directional forecast without this is a number with no claim attached. The consuming
        # gate already refused one; publication now refuses it too, rather than emitting something
        # the far side will decline.
        uncertainty=ForecastUncertainty(
            standard_error_bps=4.0,
            historical_net_edge_bps=3.5,
            historical_net_edge_standard_error_bps=1.2,
            historical_observations=180,
            assumed_round_trip_cost_bps=33.7,
        ),
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


def test_rejects_forecast_that_changes_strategy_horizon(tmp_path: Path) -> None:
    root = tmp_path / "artifacts"
    registry = ModelRegistry(str(tmp_path / "experiments.db"))
    schema, artifact, forecast = _contracts()
    forecast.horizon_minutes = 30

    with pytest.raises(ValueError, match="horizon does not match"):
        ContractPublisher(root, registry).publish_validated(
            schema, artifact, forecast, root / "model.json"
        )

    assert not (root / "current-contracts.json").exists()


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


def test_rejects_gate_names_without_validation_evidence(tmp_path: Path) -> None:
    root = tmp_path / "artifacts"
    registry = ModelRegistry(str(tmp_path / "experiments.db"))
    schema, artifact, forecast = _contracts()
    artifact.validation_evidence.pop("R3")

    with pytest.raises(ValueError, match="gates without validation evidence"):
        ContractPublisher(root, registry).publish_validated(
            schema, artifact, forecast, root / "model.json"
        )

    assert not (root / "current-contracts.json").exists()


def test_rejects_failed_validation_evidence(tmp_path: Path) -> None:
    root = tmp_path / "artifacts"
    registry = ModelRegistry(str(tmp_path / "experiments.db"))
    schema, artifact, forecast = _contracts()
    artifact.validation_evidence["R7"].passed = False

    with pytest.raises(ValueError, match="R7 did not pass"):
        ContractPublisher(root, registry).publish_validated(
            schema, artifact, forecast, root / "model.json"
        )

    assert not (root / "current-contracts.json").exists()


def test_rejects_validation_evidence_with_mismatched_gate_identity(tmp_path: Path) -> None:
    root = tmp_path / "artifacts"
    registry = ModelRegistry(str(tmp_path / "experiments.db"))
    schema, artifact, forecast = _contracts()
    artifact.validation_evidence["R11"].gate_id = "R12"

    with pytest.raises(ValueError, match="does not match gate R11"):
        ContractPublisher(root, registry).publish_validated(
            schema, artifact, forecast, root / "model.json"
        )

    assert not (root / "current-contracts.json").exists()


def test_rejects_unknown_strategy_family() -> None:
    _, artifact, _ = _contracts()

    with pytest.raises(ValueError, match="strategy_family is not registered"):
        ModelArtifact(**(artifact.model_dump() | {"strategy_family": "current-candle-winner"}))


def test_defined_risk_vertical_requires_complete_explicit_policy() -> None:
    _, artifact, _ = _contracts()
    payload = artifact.model_dump()
    payload["strategy_definition"] = payload["strategy_definition"] | {
        "execution_kind": "defined_risk_vertical",
        "option_vertical": OptionVerticalExecutionPolicy(
            minimum_days_to_expiry=7,
            maximum_days_to_expiry=60,
            strike_band_fraction=0.05,
            maximum_defined_loss=20.0,
            exit_limit_fraction=0.5,
        ).model_dump(),
    }

    parsed = ModelArtifact(**payload)

    assert parsed.strategy_definition.execution_kind == "defined_risk_vertical"
    assert parsed.strategy_definition.option_vertical is not None


def test_defined_risk_vertical_without_policy_is_rejected() -> None:
    _, artifact, _ = _contracts()
    payload = artifact.model_dump()
    payload["strategy_definition"] = payload["strategy_definition"] | {
        "execution_kind": "defined_risk_vertical",
        "option_vertical": None,
    }

    with pytest.raises(ValueError, match="requires option_vertical"):
        ModelArtifact(**payload)
