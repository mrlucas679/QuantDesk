from datetime import datetime
from typing import Any

from pydantic import BaseModel, Field


class Experiment(BaseModel):
    experiment_id: str
    hypothesis: str
    dataset_name: str
    dataset_hash: str
    feature_schema_hash: str
    model_family: str
    parameters: dict[str, Any]
    random_seed: int
    training_period: dict[str, Any]
    validation_period: dict[str, Any]
    test_period: dict[str, Any] | None = None
    metrics: dict[str, Any]
    status: str
    failure_reason: str | None = None
    artifact_ids: list[str] = Field(default_factory=list)
    git_commit: str
    config_hash: str
    timestamp: datetime
