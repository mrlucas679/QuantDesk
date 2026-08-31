import json
from datetime import UTC, datetime, timedelta
from pathlib import Path

from quantdesk_research.contracts.model_artifact import (
    EvidenceProfile,
    ValidationGateEvidence,
)
from quantdesk_research.experiments.strategy_ensemble import StrategyEvaluation, build_signal_frame
from quantdesk_research.models.contract_publication import REQUIRED_EXECUTION_GATES
from quantdesk_research.models.rule_contract_publication import publish_validated_rule_strategy


def test_passed_rule_strategy_publishes_complete_executable_bundle(tmp_path: Path) -> None:
    data_root = tmp_path / "data"
    artifacts_root = tmp_path / "artifacts"
    data_root.mkdir()
    start = datetime(2026, 1, 1, tzinfo=UTC)
    bars = [
        {
            "t": (start + timedelta(minutes=5 * index)).isoformat(),
            "o": 100 + index / 100,
            "h": 101 + index / 100,
            "l": 99 + index / 100,
            "c": 100 + index / 100,
            "v": 1000 + index,
        }
        for index in range(100)
    ]
    (data_root / "bars.json").write_text(json.dumps(bars), encoding="utf-8")
    (data_root / "manifest.json").write_text(
        json.dumps(
            {
                "symbol": "BTC/USD",
                "timeframe": "5Min",
                "dataFile": "bars.json",
                "sha256": "sha256:dataset",
            }
        ),
        encoding="utf-8",
    )
    evaluated_at = datetime.now(UTC)
    validation_evidence = {
        gate: ValidationGateEvidence(
            gate_id=gate,
            passed=True,
            evidence_ids=[f"evidence-{gate}"],
            evaluated_at=evaluated_at,
            details={"source": "test-evaluator"},
        )
        for gate in REQUIRED_EXECUTION_GATES
    }
    evaluation = StrategyEvaluation(
        name="moving_average_trend:144",
        passed=True,
        score=10,
        trade_count=80,
        mean_net_bps=25,
        lower_confidence_net_bps=10,
        win_rate=0.6,
        sharpe=0.8,
        maximum_drawdown_bps=-100,
    )
    profile = EvidenceProfile(
        evidence_id="direct-1",
        economic_hypothesis="Information is incorporated gradually.",
        counter_hypothesis="The apparent trend is noise after costs.",
        primary_evidence_ids=["source-1"],
        transfer_grade="A_Direct",
        transfer_reason="Direct instrument and venue evidence.",
    )

    artifact = publish_validated_rule_strategy(
        data_root,
        artifacts_root,
        "manifest.json",
        "CAMPAIGN-1",
        "a" * 64,
        "moving_average_trend",
        144,
        evaluation,
        profile,
        validation_evidence,
    )

    assert artifact.model_type == "deterministic_rule"
    assert artifact.strategy_definition.signal_type == "Event"
    assert artifact.strategy_definition.forecast_horizon_minutes == 720
    assert artifact.strategy_definition.exit_policy.maximum_holding_minutes == 720
    assert (artifacts_root / "current-contracts.json").exists()


def test_failed_rule_strategy_cannot_publish(tmp_path: Path) -> None:
    evaluation = StrategyEvaluation(
        "moving_average_trend:144", False, -1, 80, -2, -5, 0.4, -0.1, -100
    )
    profile = EvidenceProfile(
        evidence_id="direct-1",
        economic_hypothesis="Hypothesis",
        counter_hypothesis="Counter hypothesis",
        primary_evidence_ids=["source-1"],
        transfer_grade="A_Direct",
        transfer_reason="Direct evidence.",
    )

    import pytest

    with pytest.raises(ValueError, match="failed rule evaluation"):
        publish_validated_rule_strategy(
            tmp_path,
            tmp_path / "artifacts",
            "missing.json",
            "CAMPAIGN-1",
            "a" * 64,
            "moving_average_trend",
            144,
            evaluation,
            profile,
            {},
        )


def test_trend_state_remains_active_without_a_new_crossover() -> None:
    start = datetime(2026, 1, 1, tzinfo=UTC)
    bars = [
        {
            "t": (start + timedelta(minutes=5 * index)).isoformat(),
            "c": 100 + index,
            "v": 1000,
        }
        for index in range(100)
    ]

    frame = build_signal_frame(bars)

    assert bool(frame["trend_state"].iloc[-1])
    assert not bool(frame["moving_average_trend"].iloc[-1])
