from pathlib import Path

from quantdesk_research.evaluation.hypothesis_memory import (
    FailureReason,
    HypothesisMemory,
    RejectedHypothesis,
    classify_failure,
)


def rejection() -> RejectedHypothesis:
    return RejectedHypothesis(
        hypothesis_id="BTC-1H-BREAKOUT",
        mechanism="price discovery persists after a range break",
        reason=FailureReason.REJECTED_COSTS,
        dataset_hash="sha256:data",
        parameters={"horizon_minutes": 60},
        evidence={"gross_mean_bps": 31, "net_mean_bps": -29},
        regime="all",
        cost_scenario="conservative-60bps",
    )


def test_rejected_hypothesis_is_durable_and_duplicate_safe(tmp_path: Path) -> None:
    memory = HypothesisMemory(tmp_path / "experiments.db")

    assert memory.record(rejection())
    assert not HypothesisMemory(tmp_path / "experiments.db").record(rejection())
    assert HypothesisMemory(tmp_path / "experiments.db").contains(rejection())

    rows = memory.list_rejections()
    assert rows[0]["reason"] == "RejectedCosts"
    assert rows[0]["next_action"] == "increase_horizon_or_reduce_turnover"


def test_failure_classification_routes_by_lowest_level_cause() -> None:
    assert classify_failure(
        gross_mean_bps=31, net_mean_bps=-29, trade_count=100, minimum_trades=60
    ) is FailureReason.REJECTED_COSTS
    assert classify_failure(
        gross_mean_bps=100, net_mean_bps=40, trade_count=20, minimum_trades=60
    ) is FailureReason.INSUFFICIENT_TRADES
    assert classify_failure(
        gross_mean_bps=-1, net_mean_bps=-61, trade_count=100, minimum_trades=60
    ) is FailureReason.NO_RAW_EDGE
    assert classify_failure(
        gross_mean_bps=100,
        net_mean_bps=40,
        trade_count=100,
        minimum_trades=60,
        parameter_stable=False,
    ) is FailureReason.PARAMETER_FRAGILITY
