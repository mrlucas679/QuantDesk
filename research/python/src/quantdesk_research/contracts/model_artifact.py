from datetime import datetime
from typing import Any

from pydantic import BaseModel, field_validator

EXECUTABLE_STRATEGY_FAMILIES = frozenset(
    {
        "price_volume_directional",
        "weekly_time_series_momentum",
        "four_week_time_series_momentum",
        "dual_horizon_momentum",
        "four_week_breakout",
        "donchian_breakout",
        "moving_average_trend",
        "bollinger_reversion",
        "rsi_reversion",
        "volatility_breakout",
        "regime_ensemble",
        "volume_confirmed_breakout",
        "compression_breakout",
    }
)


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
    strategy_family: str

    @field_validator("strategy_family")
    @classmethod
    def strategy_family_must_be_executable(cls, value: str) -> str:
        """Reject artifacts that cannot map to an application-owned strategy family."""
        if value not in EXECUTABLE_STRATEGY_FAMILIES:
            raise ValueError("strategy_family is not registered for execution")
        return value

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
