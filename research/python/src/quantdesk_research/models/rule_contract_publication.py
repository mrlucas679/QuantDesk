"""Publication of deterministic rule strategies through the common execution contract."""

import hashlib
import json
from datetime import UTC, datetime
from pathlib import Path
from typing import Any

from quantdesk_research.contracts.feature_schema import FeatureSchema
from quantdesk_research.contracts.forecast import Forecast, ForecastUncertainty
from quantdesk_research.contracts.model_artifact import (
    EvidenceProfile,
    ExitPolicyDefinition,
    ModelArtifact,
    StrategyDefinition,
    ValidationGateEvidence,
)
from quantdesk_research.data.manifest_keys import require_manifest_value
from quantdesk_research.experiments.strategy_ensemble import (
    StrategyEvaluation,
    build_signal_frame,
)
from quantdesk_research.models.contract_publication import ContractPublisher
from quantdesk_research.models.model_registry import ModelRegistry

EVENT_FAMILIES = frozenset({"moving_average_trend"})


def publish_validated_rule_strategy(
    data_root: Path,
    artifacts_root: Path,
    manifest_name: str,
    campaign_id: str,
    campaign_fingerprint: str,
    family: str,
    horizon_bars: int,
    evaluation: StrategyEvaluation,
    evidence_profile: EvidenceProfile,
    validation_evidence: dict[str, ValidationGateEvidence],
    round_trip_cost_bps: float,
) -> ModelArtifact:
    """Publish one passed deterministic strategy with exact executable semantics."""
    if not evaluation.passed:
        raise ValueError("A failed rule evaluation cannot be promoted.")
    if evaluation.name.split(":", maxsplit=1)[0] != family:
        raise ValueError("Rule evaluation does not match the requested strategy family.")
    manifest = _load_object(data_root / manifest_name)
    bars = _load_array(data_root / str(require_manifest_value(manifest, "data_file")))
    timeframe = str(require_manifest_value(manifest, "timeframe"))
    bar_minutes = _bar_duration_minutes(timeframe)
    horizon_minutes = horizon_bars * bar_minutes
    signals = build_signal_frame(bars)
    if family not in signals.columns:
        raise ValueError("Rule strategy family has no causal signal implementation.")

    definition = StrategyDefinition(
        symbol=str(require_manifest_value(manifest, "symbol")),
        bar_duration_minutes=bar_minutes,
        forecast_horizon_minutes=horizon_minutes,
        entry_rule_version=f"{family}-v1",
        signal_type="Event" if family in EVENT_FAMILIES else "State",
        parameters=_semantic_parameters(family),
        exit_policy=ExitPolicyDefinition(
            policy_version=f"{family}-managed-v1",
            maximum_holding_minutes=horizon_minutes,
            exit_on_thesis_invalidation=True,
            exit_on_regime_change=True,
        ),
    )
    payload = {
        "campaign_id": campaign_id,
        "campaign_fingerprint": campaign_fingerprint,
        "family": family,
        "strategy_definition": definition.model_dump(mode="json"),
    }
    encoded = json.dumps(payload, sort_keys=True).encode("utf-8")
    artifact_hash = hashlib.sha256(encoded).hexdigest()
    artifact_id = f"{campaign_id}-{family}-{horizon_bars}-{campaign_fingerprint[:12]}"
    model_file = artifacts_root / f"{artifact_id}-strategy.json"
    artifacts_root.mkdir(parents=True, exist_ok=True)
    model_file.write_bytes(encoded)

    schema_document = {
        "schema_version": "deterministic-rule-v1",
        "feature_names": [family],
        "dtypes": {family: "bool"},
        "normalization": {},
        "lookback_periods": max(1, max(_semantic_parameters(family).values())),
        "source_requirements": ["alpaca_ohlcv"],
    }
    feature_hash = hashlib.sha256(
        json.dumps(schema_document, sort_keys=True).encode("utf-8")
    ).hexdigest()
    schema = FeatureSchema(
        schema_version="deterministic-rule-v1",
        feature_names=[family],
        dtypes={family: "bool"},
        normalization={},
        lookback_periods=max(1, max(_semantic_parameters(family).values())),
        source_requirements=["alpaca_ohlcv"],
        feature_hash=feature_hash,
    )
    evaluated_at = datetime.now(UTC)
    artifact = ModelArtifact(
        artifact_id=artifact_id,
        model_id=f"{campaign_id}-{family}",
        model_type="deterministic_rule",
        model_version="v1",
        strategy_family=family,
        strategy_definition=definition,
        feature_schema_hash=feature_hash,
        dataset_hash=str(require_manifest_value(manifest, "sha256")),
        training_window={},
        calibration_window=None,
        test_window={"campaign_fingerprint": campaign_fingerprint},
        parameters={"horizon_bars": horizon_bars},
        random_seed=0,
        metrics=evaluation.__dict__,
        evidence_grade=evidence_profile.transfer_grade,
        evidence_profile=evidence_profile,
        validation_gates=sorted(validation_evidence),
        validation_evidence=validation_evidence,
        support_domain={"instrument": require_manifest_value(manifest, "symbol"), "timeframe": timeframe},
        git_commit="working-tree",
        config_hash=campaign_fingerprint,
        creation_timestamp=evaluated_at,
        artifact_hash=artifact_hash,
    )
    active = bool(signals[family].iloc[-1])
    forecast = Forecast(
        expert_id=artifact.model_id,
        model_id=artifact.model_id,
        model_version=artifact.model_version,
        instrument=definition.symbol,
        as_of_time=datetime.fromisoformat(str(signals["t"].iloc[-1])),
        forecast_family="directional_return_bps",
        horizon_minutes=horizon_minutes,
        point_forecast=evaluation.mean_net_bps if active else 0.0,
        confidence=0.75,
        uncertainty=_uncertainty(evaluation, round_trip_cost_bps),
        calibration_status="independent_validation_pass",
        support_domain_status="in_domain",
        feature_schema_hash=feature_hash,
        artifact_hash=artifact_hash,
        status="valid",
        reason_code=None if active else "NO_CURRENT_SIGNAL",
    )
    registry = ModelRegistry(str(data_root / "experiments.db"))
    ContractPublisher(artifacts_root, registry).publish_validated(
        schema, artifact, forecast, model_file
    )
    return artifact


def _semantic_parameters(family: str) -> dict[str, int]:
    definitions = {
        "donchian_breakout": {"lookback_minutes": 240},
        "moving_average_trend": {"fast_horizon_minutes": 60, "slow_horizon_minutes": 240},
        "bollinger_reversion": {"lookback_minutes": 240, "standard_deviations_x100": 200},
        "rsi_reversion": {"lookback_minutes": 70, "threshold": 25},
        "volatility_breakout": {"short_horizon_minutes": 60, "long_horizon_minutes": 240},
        "regime_ensemble": {"short_horizon_minutes": 60, "long_horizon_minutes": 240},
        "volume_confirmed_breakout": {"lookback_minutes": 240, "volume_z_x100": 200},
        "compression_breakout": {"short_horizon_minutes": 60, "long_horizon_minutes": 240},
        "weekly_time_series_momentum": {"lookback_minutes": 10_080},
        "four_week_time_series_momentum": {"lookback_minutes": 40_320},
        "dual_horizon_momentum": {"fast_horizon_minutes": 10_080, "slow_horizon_minutes": 40_320},
        "four_week_breakout": {"lookback_minutes": 40_320},
        "trend_state": {"fast_horizon_minutes": 60, "slow_horizon_minutes": 240},
    }
    try:
        return definitions[family]
    except KeyError as error:
        raise ValueError("Rule strategy family has no semantic parameter definition.") from error


def _bar_duration_minutes(timeframe: str) -> int:
    if timeframe == "5Min":
        return 5
    if timeframe in {"1Day", "Day"}:
        return 24 * 60
    raise ValueError("Unsupported rule-strategy bar duration.")


def _load_object(path: Path) -> dict[str, Any]:
    document: Any = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(document, dict):
        raise TypeError("Expected a JSON object.")
    return {str(key): value for key, value in document.items()}


def _load_array(path: Path) -> list[dict[str, Any]]:
    document: Any = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(document, list) or not all(isinstance(item, dict) for item in document):
        raise ValueError("Expected an array of bar objects.")
    return [{str(key): value for key, value in item.items()} for item in document]


# The bound in StrategyEvaluation is a two-sided 95% interval on the mean: mean - 1.96 * se. The
# standard error is recovered from it rather than recomputed, so the published figure can never
# disagree with the one the gates were applied against.
_TWO_SIDED_95 = 1.96


def _uncertainty(
    evaluation: StrategyEvaluation, round_trip_cost_bps: float
) -> ForecastUncertainty:
    """State how wrong this forecast could be, and what the family actually earned.

    A deterministic rule has no per-bar model output distinct from its own conditional history: when
    the rule fires, the expectation *is* the mean net return across the occasions it fired before.
    So the current signal and the historical edge are the same estimate here, and saying so plainly
    is the honest description. What was missing was never a second number -- it was the error bar on
    the first one, without which a point estimate reads as a fact.

    ``assumed_round_trip_cost_bps`` travels with them because ``point_forecast`` is already net of
    it. An execution plane that subtracts its own measured cost from an already-net figure charges
    the same cost twice and refuses every trade, so the deduction has to be reversible.
    """
    standard_error = max(
        (evaluation.mean_net_bps - evaluation.lower_confidence_net_bps) / _TWO_SIDED_95, 0.0
    )
    return ForecastUncertainty(
        standard_error_bps=standard_error,
        historical_net_edge_bps=evaluation.mean_net_bps,
        historical_net_edge_standard_error_bps=standard_error,
        historical_observations=evaluation.trade_count,
        assumed_round_trip_cost_bps=round_trip_cost_bps,
    )
