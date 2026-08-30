from datetime import UTC, datetime
from typing import Any

from pydantic import BaseModel, Field

from quantdesk_research.contracts.model_artifact import ModelArtifact


class PromotionEvidence(BaseModel):
    artifact_id: str
    model_id: str
    model_version: str

    statistical_metrics: dict[str, Any]
    economic_metrics: dict[str, Any]
    actionability_score: float

    calibration_report: dict[str, Any]
    support_domain: dict[str, Any]

    robustness_summary: dict[str, Any]
    ablation_summary: dict[str, Any] | None = None

    pbo_value: float | None = None
    deflated_sharpe: float | None = None

    timestamp: datetime = Field(default_factory=lambda: datetime.now(UTC))


def generate_promotion_evidence(
    artifact: ModelArtifact,
    economic_utility: dict[str, Any],
    actionability_score: float,
    robustness_results: dict[str, Any],
    pbo: float | None = None,
    dsr: float | None = None,
) -> PromotionEvidence:
    """
    Synthesizes research evidence for a model candidate to be promoted.
    """
    return PromotionEvidence(
        artifact_id=artifact.artifact_id,
        model_id=artifact.model_id,
        model_version=artifact.model_version,
        statistical_metrics=artifact.metrics,
        economic_metrics=economic_utility,
        actionability_score=actionability_score,
        calibration_report={},  # To be filled
        support_domain=artifact.support_domain,
        robustness_summary=robustness_results,
        pbo_value=pbo,
        deflated_sharpe=dsr,
    )
