"""Atomic publication of validated research contracts for the C# execution plane."""

import json
import os
from pathlib import Path
from typing import Any
from uuid import uuid4

from quantdesk_research.contracts.feature_schema import FeatureSchema
from quantdesk_research.contracts.forecast import Forecast
from quantdesk_research.contracts.model_artifact import ModelArtifact
from quantdesk_research.models.model_registry import ModelRegistry

REQUIRED_EXECUTION_GATES = frozenset({"R0", "R1", "R2", "R3", "R4", "R5", "R6", "R7", "R11", "R12"})


class ContractPublisher:
    """Publishes a complete, hash-linked contract bundle or leaves the prior bundle intact."""

    def __init__(self, artifacts_root: Path, registry: ModelRegistry) -> None:
        self._root = artifacts_root
        self._registry = registry
        self._root.mkdir(parents=True, exist_ok=True)

    def publish_validated(
        self,
        schema: FeatureSchema,
        artifact: ModelArtifact,
        forecast: Forecast,
        artifact_path: Path,
    ) -> None:
        """Persist a verified bundle before making it visible and promoting its registry entry."""
        self._validate(schema, artifact, forecast)
        schema_name = f"{artifact.artifact_id}-feature-schema.json"
        artifact_name = f"{artifact.artifact_id}-model-artifact.json"
        forecast_name = f"{artifact.artifact_id}-forecast.json"
        self._write_atomic(schema_name, schema.model_dump(mode="json"))
        self._write_atomic(artifact_name, artifact.model_dump(mode="json"))
        self._write_atomic(forecast_name, forecast.model_dump(mode="json"))
        self._write_atomic(
            "current-contracts.json",
            {
                "FeatureSchema": schema_name,
                "ModelArtifact": artifact_name,
                "Forecast": forecast_name,
            },
        )
        self._registry.register_model(artifact, artifact_path)
        self._registry.update_promotion_state(artifact.artifact_id, "VALIDATED")

    @staticmethod
    def _validate(schema: FeatureSchema, artifact: ModelArtifact, forecast: Forecast) -> None:
        if forecast.status.lower() != "valid":
            raise ValueError("Only valid forecasts may be published.")
        if forecast.forecast_family.lower() != "directional_return_bps":
            raise ValueError("Published forecast family must be directional_return_bps.")
        if artifact.feature_schema_hash != schema.feature_hash:
            raise ValueError("Artifact feature schema hash does not match the schema.")
        if forecast.feature_schema_hash != schema.feature_hash:
            raise ValueError("Forecast feature schema hash does not match the schema.")
        if forecast.model_id != artifact.model_id or forecast.model_version != artifact.model_version:
            raise ValueError("Forecast model identity does not match the artifact.")
        definition = artifact.strategy_definition
        if definition.forecast_horizon_minutes % definition.bar_duration_minutes != 0:
            raise ValueError("Strategy horizon is not divisible by its bar duration.")
        if forecast.instrument != definition.symbol:
            raise ValueError("Forecast instrument does not match the executable strategy definition.")
        if forecast.horizon_minutes != definition.forecast_horizon_minutes:
            raise ValueError("Forecast horizon does not match the executable strategy definition.")
        if forecast.artifact_hash != artifact.artifact_hash:
            raise ValueError("Forecast artifact hash does not match the artifact.")
        profile = artifact.evidence_profile
        if not profile.primary_evidence_ids or not all(profile.primary_evidence_ids):
            raise ValueError("Published artifact requires primary evidence identifiers.")
        if profile.transfer_grade not in {"A_Direct", "B_Close"}:
            raise ValueError("Published artifact evidence transfer is not execution-eligible.")
        missing_gates = REQUIRED_EXECUTION_GATES.difference(artifact.validation_gates)
        if missing_gates:
            raise ValueError(f"Published artifact is missing required validation gates: {sorted(missing_gates)}")
        unsupported_claims = set(artifact.validation_gates).difference(artifact.validation_evidence)
        if unsupported_claims:
            raise ValueError(
                f"Published artifact has gates without validation evidence: {sorted(unsupported_claims)}"
            )
        for gate_id in REQUIRED_EXECUTION_GATES:
            evidence = artifact.validation_evidence.get(gate_id)
            if evidence is None:
                raise ValueError(f"Published artifact is missing validation evidence for {gate_id}.")
            if evidence.gate_id != gate_id:
                raise ValueError(f"Validation evidence key does not match gate {gate_id}.")
            if not evidence.passed:
                raise ValueError(f"Validation evidence for {gate_id} did not pass.")

    def _write_atomic(self, file_name: str, document: dict[str, Any]) -> None:
        target = self._root / file_name
        temporary = self._root / f".{file_name}.{uuid4().hex}.tmp"
        temporary.write_text(json.dumps(document, sort_keys=True), encoding="utf-8")
        os.replace(temporary, target)
