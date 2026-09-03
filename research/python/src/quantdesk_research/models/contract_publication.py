"""Atomic publication of validated research contracts for the C# execution plane.

What changed, and why it is not a relaxation
--------------------------------------------
This publisher accepted only ``directional_return_bps``, so the volatility and regime models had no
honest route across the boundary at all. The reason was never arbitrary: every gate here -- R0
through R12, transfer grade, primary evidence, net edge after costs -- answers whether a signal is
worth trading, and that question only applies to a forecast which licenses a trade.

A conditional variance does not license one. It sizes a position, or refuses it, or ends it early.
Requiring it to show a positive net edge after round-trip costs is not a stricter standard; it is
the wrong question, and every honest answer to it is "not applicable" -- which is how a gate becomes
a form to fill in.

So the families are separated rather than the gates weakened. A directional forecast still carries
every execution gate it carried before, and it is still the only family that may reach execution.
The advisory families carry the gates that mean something for them, and ``forecast_family`` decides
which set applies instead of a single hard-coded name deciding whether publication is possible.

Model publication is separated from forecast publication for the same reason they are separate
contracts: a fitted model can exist with no strategy entitled to use it, and a strategy can be
licensed with no fitted model behind it.
"""

import json
import os
from pathlib import Path
from typing import Any
from uuid import uuid4

from quantdesk_research.contracts.feature_schema import FeatureSchema
from quantdesk_research.contracts.forecast import Forecast
from quantdesk_research.contracts.forecast_family import (
    ForecastFamilySpec,
    family_of,
)
from quantdesk_research.contracts.model_artifact import ModelArtifact
from quantdesk_research.models.model_registry import ModelRegistry
from quantdesk_research.models.runtime_artifact import RuntimeInferenceArtifact

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

        # Named rather than assumed. Exactly one family may license a trade, and a family added
        # later cannot do so by default -- reaching that decision means arriving at
        # forecast_family.py and reading why.
        spec = family_of(forecast.forecast_family)
        if not spec.licenses_execution:
            raise ValueError(
                f"forecast family {spec.name!r} informs a decision but does not license a trade; "
                "publish it with publish_forecast rather than through the execution bundle"
            )
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

        # Last, once the bundle is internally consistent. A forecast pointing at the wrong artifact
        # should be told that, not told its uncertainty block is missing.
        spec.validate(forecast)

    def publish_forecast(
        self, schema: FeatureSchema, forecast: Forecast, artifact_hash: str
    ) -> str:
        """Publish a forecast of any registered family, checked against that family's rules.

        The advisory families reach the runtime through here. They are held to their own shape --
        a variance that cannot be negative and states its units, a regime posterior that sums to one
        and names its states -- rather than to the execution gates, which ask a question they do not
        answer.
        """
        spec = family_of(forecast.forecast_family)
        if forecast.status.lower() != "valid":
            raise ValueError("Only valid forecasts may be published.")
        if forecast.feature_schema_hash != schema.feature_hash:
            raise ValueError("Forecast feature schema hash does not match the schema.")
        if forecast.artifact_hash != artifact_hash:
            raise ValueError("Forecast artifact hash does not match the artifact it came from.")
        spec.validate(forecast)

        name = f"{forecast.model_id}-{forecast.forecast_family}-forecast.json"
        self._write_atomic(name, forecast.model_dump(mode="json"))
        return name

    def publish_model(self, artifact: RuntimeInferenceArtifact) -> str:
        """Publish a fitted model the runtime can load, or refuse it.

        Separate from strategy publication because they are separate lifecycles. This carries the
        numbers an inference path needs and the parity cases that prove a reimplementation of it
        agrees with the library; it carries no licence to trade, and gaining one is a different
        decision taken elsewhere.
        """
        if not artifact.hash_matches():
            raise ValueError(
                "artifact hash does not cover its own contents; it was edited after sealing"
            )
        if not artifact.parity.cases:
            raise ValueError(
                "a model with no parity cases cannot be verified by the runtime, and an unverified "
                "reimplementation is what the contract exists to prevent"
            )

        name = f"{artifact.artifact_id}-fitted-model.json"
        self._write_atomic(name, artifact.model_dump(mode="json"))
        return name

    @staticmethod
    def family(name: str) -> ForecastFamilySpec:
        """The spec for a family, so callers need not import the registry to ask."""
        return family_of(name)

    def _write_atomic(self, file_name: str, document: dict[str, Any]) -> None:
        target = self._root / file_name
        temporary = self._root / f".{file_name}.{uuid4().hex}.tmp"
        temporary.write_text(json.dumps(document, sort_keys=True), encoding="utf-8")
        os.replace(temporary, target)
