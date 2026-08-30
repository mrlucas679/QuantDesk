from datetime import datetime
from typing import Any

from pydantic import BaseModel


class EvidenceProfile(BaseModel):
    """Research provenance required before a model may become execution evidence."""

    evidence_id: str
    economic_hypothesis: str
    counter_hypothesis: str
    primary_evidence_ids: list[str]
    transfer_grade: str
    transfer_reason: str


class ModelArtifact(BaseModel):
    artifact_id: str
    model_id: str
    model_type: str
    model_version: str

    feature_schema_hash: str
    dataset_hash: str

    training_window: dict[str, Any]
    calibration_window: dict[str, Any] | None = None
    test_window: dict[str, Any] | None = None

    parameters: dict[str, Any]
    random_seed: int

    metrics: dict[str, Any]
    evidence_grade: str
    evidence_profile: EvidenceProfile
    validation_gates: list[str]
    support_domain: dict[str, Any]

    git_commit: str
    config_hash: str
    creation_timestamp: datetime
    artifact_hash: str
