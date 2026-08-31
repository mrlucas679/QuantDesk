from datetime import datetime
from typing import Any, Literal

from pydantic import BaseModel, field_validator, model_validator

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
        "trend_state",
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


class ValidationGateEvidence(BaseModel):
    """Auditable result produced by a concrete validation-gate evaluation."""

    gate_id: str
    passed: bool
    evidence_ids: list[str]
    evaluated_at: datetime
    details: dict[str, Any]

    @field_validator("evidence_ids")
    @classmethod
    def evidence_ids_must_be_present(cls, value: list[str]) -> list[str]:
        """Prevent a bare pass flag from masquerading as validation evidence."""
        if not value or not all(item.strip() for item in value):
            raise ValueError("validation gate evidence requires evidence identifiers")
        return value


class ExitPolicyDefinition(BaseModel):
    """Exact position-management semantics validated by research."""

    policy_version: str
    maximum_holding_minutes: int
    exit_on_thesis_invalidation: bool
    exit_on_regime_change: bool

    @field_validator("maximum_holding_minutes")
    @classmethod
    def holding_period_must_be_positive(cls, value: int) -> int:
        """Reject an artifact that cannot own a meaningful managed lifecycle."""
        if value <= 0:
            raise ValueError("maximum_holding_minutes must be positive")
        return value


class OptionVerticalExecutionPolicy(BaseModel):
    """Research-approved limits required to create one defined-risk debit vertical."""

    minimum_days_to_expiry: int
    maximum_days_to_expiry: int
    strike_band_fraction: float
    maximum_defined_loss: float
    exit_limit_fraction: float

    @model_validator(mode="after")
    def limits_are_safe(self) -> OptionVerticalExecutionPolicy:
        """Reject an option policy that leaves selection, downside, or exit economics implicit."""
        if self.minimum_days_to_expiry <= 0 or self.maximum_days_to_expiry < self.minimum_days_to_expiry:
            raise ValueError("option expiry window is invalid")
        if not 0 < self.strike_band_fraction <= 1:
            raise ValueError("option strike_band_fraction must be within (0, 1]")
        if self.maximum_defined_loss <= 0 or not 0 < self.exit_limit_fraction <= 1:
            raise ValueError("option loss and exit limits must be positive bounded fractions")
        return self


class StrategyDefinition(BaseModel):
    """Resolution-independent executable definition owned by the artifact."""

    symbol: str
    bar_duration_minutes: int
    forecast_horizon_minutes: int
    entry_rule_version: str
    signal_type: str
    parameters: dict[str, Any]
    exit_policy: ExitPolicyDefinition
    execution_kind: Literal["spot", "defined_risk_vertical"] = "spot"
    option_vertical: OptionVerticalExecutionPolicy | None = None

    @field_validator("signal_type")
    @classmethod
    def signal_type_must_be_supported(cls, value: str) -> str:
        """Keep event and persistent-state semantics explicit at the boundary."""
        if value not in {"Event", "State"}:
            raise ValueError("signal_type must be Event or State")
        return value

    @field_validator("bar_duration_minutes", "forecast_horizon_minutes")
    @classmethod
    def horizons_must_be_positive(cls, value: int) -> int:
        """Reject non-positive semantic durations at the publication boundary."""
        if value <= 0:
            raise ValueError("strategy durations must be positive")
        return value

    @model_validator(mode="after")
    def execution_policy_matches_kind(self) -> StrategyDefinition:
        """Ensure an options artifact cannot reach runtime without complete vertical semantics."""
        if self.execution_kind == "spot" and self.option_vertical is not None:
            raise ValueError("spot strategy must not carry an option vertical policy")
        if self.execution_kind == "defined_risk_vertical" and self.option_vertical is None:
            raise ValueError("defined-risk vertical strategy requires option_vertical policy")
        return self


class ModelArtifact(BaseModel):
    artifact_id: str
    model_id: str
    model_type: str
    model_version: str
    strategy_family: str
    strategy_definition: StrategyDefinition

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
    validation_evidence: dict[str, ValidationGateEvidence]
    support_domain: dict[str, Any]

    git_commit: str
    config_hash: str
    creation_timestamp: datetime
    artifact_hash: str
