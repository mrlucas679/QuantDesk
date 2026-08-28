from datetime import datetime

from pydantic import BaseModel


class Experiment(BaseModel):
    experiment_id: str
    hypothesis: str
    dataset_name: str
    dataset_hash: str
    feature_schema_hash: str
    model_family: str
    parameters: dict
    random_seed: int
    training_period: dict
    validation_period: dict
    test_period: dict | None = None
    metrics: dict
    status: str
    failure_reason: str | None = None
    artifact_ids: list[str] = []
    git_commit: str
    config_hash: str
    timestamp: datetime
