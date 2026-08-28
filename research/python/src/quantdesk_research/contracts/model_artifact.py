from datetime import datetime

from pydantic import BaseModel


class ModelArtifact(BaseModel):
    artifact_id: str
    model_id: str
    model_type: str
    model_version: str

    feature_schema_hash: str
    dataset_hash: str

    training_window: dict
    calibration_window: dict | None = None
    test_window: dict | None = None

    parameters: dict
    random_seed: int

    metrics: dict
    evidence_grade: str
    support_domain: dict

    git_commit: str
    config_hash: str
    creation_timestamp: datetime
    artifact_hash: str
