"""The R-gates, and the property that stops them becoming a form to fill in.

Both promotion paths previously declared ``validation_gates=["R0", "R1", "R2", "R4"]`` with an empty
evidence map, which the publisher refuses -- so neither could ever have published, and nothing
noticed because no candidate has yet cleared the statistical gates that come first.

The tempting repair is to declare all ten and mark them passed. These tests exist to make that
impossible: every gate must be a function of a measurement, and a gate with no evidence must fail.
"""

from __future__ import annotations

import json
from datetime import UTC, datetime, timedelta
from pathlib import Path

import pytest

from quantdesk_research.contracts.model_artifact import EvidenceProfile
from quantdesk_research.validation.gate_evaluation import (
    REQUIRED_GATES,
    CandidateMeasurements,
    RuntimeAttestation,
    describe_failures,
    evaluate_required_gates,
    failing_gates,
)


def _profile(transfer_grade: str = "A_Direct") -> EvidenceProfile:
    return EvidenceProfile(
        evidence_id="TEST-001",
        economic_hypothesis="A short-horizon drift persists long enough to pay the round trip.",
        counter_hypothesis="The drift is noise and costs erase it.",
        primary_evidence_ids=["preregistered-holdout"],
        transfer_grade=transfer_grade,
        transfer_reason="Prospective holdout on the traded instrument.",
    )


def _measurements(**overrides: object) -> CandidateMeasurements:
    defaults: dict[str, object] = {
        "strategy_name": "donchian_breakout:48",
        "lower_confidence_net_bps": 12.5,
        "baseline_lower_confidence_net_bps": 3.0,
        "trade_count": 120,
        "sharpe": 0.9,
        "maximum_drawdown_bps": -450.0,
        "round_trip_cost_bps": 60.0,
        "alpha_life_minutes": 240.0,
        "labels_purged_and_embargoed": True,
        "fit_ends_before_prediction": True,
        "trials_evaluated": 32,
        "seeds_evaluated": 1,
        "walk_forward_folds": 4,
        "evidence_class": "PassiveHistoricalReplay",
        "dataset_hash": "sha256:abc",
    }
    defaults.update(overrides)
    return CandidateMeasurements(**defaults)  # type: ignore[arg-type]


def _attestation(**overrides: object) -> RuntimeAttestation:
    defaults: dict[str, object] = {
        "attested_at": datetime.now(UTC),
        "deterministic_client_order_ids": True,
        "ambiguous_submit_resolves_unknown": True,
        "reservation_before_submit": True,
        "reconciliation_healthy": True,
        "pending_order_invalidation": True,
        "bounded_queues": True,
        "no_reconnect_leak": True,
        "paper_endpoint_verified": True,
        "decision_path_p99_milliseconds": 45.0,
        "data_age_p99_milliseconds": 600.0,
        "replay_trace_hash": "9c6abc3a",
    }
    defaults.update(overrides)
    return RuntimeAttestation(**defaults)  # type: ignore[arg-type]


# ------------------------------------------------------------------ the refusal that matters most


def test_no_runtime_attestation_fails_the_three_gates_only_the_runtime_can_answer() -> None:
    # Research cannot see the execution plane. Passing R5, R11 or R12 from here would be an
    # assertion about a process this one has never observed.
    evidence = evaluate_required_gates(_measurements(), _profile(), None, "candidate")

    assert failing_gates(evidence) == ["R11", "R12", "R5"]
    assert "no runtime attestation" in describe_failures(evidence)


def test_a_stale_attestation_is_not_evidence_about_the_system_running_now() -> None:
    stale = _attestation(attested_at=datetime.now(UTC) - timedelta(hours=9))

    evidence = evaluate_required_gates(_measurements(), _profile(), stale, "candidate")

    assert "R11" in failing_gates(evidence)
    assert evidence["R11"].details["reason"] == "runtime attestation is stale"


def test_every_required_gate_is_evaluated() -> None:
    evidence = evaluate_required_gates(_measurements(), _profile(), _attestation(), "candidate")

    assert set(evidence) == set(REQUIRED_GATES)
    assert all(item.evidence_ids for item in evidence.values())


def test_a_fully_evidenced_candidate_passes_every_gate() -> None:
    # The ladder must have a top rung. A set of gates nothing can ever satisfy is as useless as one
    # that waves everything through, and would leave the system unable to promote a real edge.
    evidence = evaluate_required_gates(_measurements(), _profile(), _attestation(), "candidate")

    assert failing_gates(evidence) == []


# --------------------------------------------------------------------- each gate refuses for cause


def test_r4_refuses_an_edge_that_does_not_survive_costs() -> None:
    evidence = evaluate_required_gates(
        _measurements(lower_confidence_net_bps=-61.9), _profile(), _attestation(), "candidate"
    )

    assert not evidence["R4"].passed
    assert evidence["R4"].details["round_trip_cost_bps"] == 60.0


def test_r2_refuses_a_candidate_that_does_not_beat_its_baseline() -> None:
    evidence = evaluate_required_gates(
        _measurements(lower_confidence_net_bps=3.0, baseline_lower_confidence_net_bps=5.0),
        _profile(),
        _attestation(),
        "candidate",
    )

    assert not evidence["R2"].passed


def test_r2_refuses_when_no_baseline_was_measured_rather_than_passing_by_default() -> None:
    evidence = evaluate_required_gates(
        _measurements(baseline_lower_confidence_net_bps=None),
        _profile(),
        _attestation(),
        "candidate",
    )

    assert not evidence["R2"].passed
    assert evidence["R2"].details["reason"] == "no baseline measured"


def test_r0_refuses_evidence_that_does_not_transfer_to_what_is_traded() -> None:
    evidence = evaluate_required_gates(
        _measurements(), _profile(transfer_grade="C_Weak"), _attestation(), "candidate"
    )

    assert not evidence["R0"].passed


def test_r1_refuses_a_backtest_whose_labels_were_not_purged() -> None:
    evidence = evaluate_required_gates(
        _measurements(labels_purged_and_embargoed=False), _profile(), _attestation(), "candidate"
    )

    assert not evidence["R1"].passed


def test_r3_refuses_a_single_window_result() -> None:
    evidence = evaluate_required_gates(
        _measurements(walk_forward_folds=1), _profile(), _attestation(), "candidate"
    )

    assert not evidence["R3"].passed


def test_r5_refuses_alpha_that_decays_faster_than_the_runtime_can_act() -> None:
    # A one-minute signal against a decision path measured in minutes is not tradable, however good
    # the backtest looks.
    slow = _attestation(decision_path_p99_milliseconds=90_000.0, data_age_p99_milliseconds=60_000.0)

    evidence = evaluate_required_gates(
        _measurements(alpha_life_minutes=1.0), _profile(), slow, "candidate"
    )

    assert not evidence["R5"].passed


def test_r5_refuses_when_the_runtime_has_not_measured_its_own_latency() -> None:
    unmeasured = _attestation(decision_path_p99_milliseconds=None)

    evidence = evaluate_required_gates(_measurements(), _profile(), unmeasured, "candidate")

    assert not evidence["R5"].passed
    assert "decision path" in str(evidence["R5"].details["reason"])


def test_r7_reads_a_drawdown_by_its_depth_whichever_sign_the_evaluator_uses() -> None:
    # The rolling experiment reports depth as positive, the strategy cohort as a negative trough.
    # A signed comparison against an upper bound waves every candidate from one of them through.
    negative = evaluate_required_gates(
        _measurements(maximum_drawdown_bps=-9_000.0), _profile(), _attestation(), "candidate"
    )
    positive = evaluate_required_gates(
        _measurements(maximum_drawdown_bps=9_000.0), _profile(), _attestation(), "candidate"
    )

    assert not negative["R7"].passed
    assert not positive["R7"].passed


def test_r11_refuses_when_the_runtime_reports_reconciliation_unhealthy() -> None:
    evidence = evaluate_required_gates(
        _measurements(), _profile(), _attestation(reconciliation_healthy=False), "candidate"
    )

    assert not evidence["R11"].passed
    assert evidence["R11"].details["reconciliation_healthy"] is False


def test_r12_refuses_when_no_session_has_been_proven_to_replay() -> None:
    evidence = evaluate_required_gates(
        _measurements(), _profile(), _attestation(replay_trace_hash=None), "candidate"
    )

    assert not evidence["R12"].passed
    assert evidence["R12"].details["replay_reproduced"] is False


# ---------------------------------------------------------------------------- reading the document


def test_the_attestation_is_read_from_what_the_runtime_writes(tmp_path: Path) -> None:
    path = tmp_path / "runtime-attestation.json"
    path.write_text(
        json.dumps(
            {
                "attestedAt": "2026-09-03T10:00:00+00:00",
                "deterministicClientOrderIds": True,
                "ambiguousSubmitResolvesUnknown": False,
                "reservationBeforeSubmit": True,
                "reconciliationHealthy": True,
                "pendingOrderInvalidation": False,
                "boundedQueues": False,
                "noReconnectLeak": False,
                "paperEndpointVerified": True,
                "decisionPathP99Milliseconds": 45.0,
                "dataAgeP99Milliseconds": 600.0,
                "replayTraceHash": "9c6abc3a",
                "notMeasured": ["boundedQueues", "noReconnectLeak"],
            }
        ),
        encoding="utf-8",
    )

    attestation = RuntimeAttestation.load(path)

    assert attestation is not None
    assert attestation.paper_endpoint_verified
    assert not attestation.bounded_queues
    assert attestation.replay_trace_hash == "9c6abc3a"


@pytest.mark.parametrize("content", ["", "{ not json", '{"attestedAt": "2026-09-03T10:00:00Z"}'])
def test_an_unreadable_attestation_is_absent_rather_than_trusted(
    tmp_path: Path, content: str
) -> None:
    path = tmp_path / "runtime-attestation.json"
    path.write_text(content, encoding="utf-8")

    assert RuntimeAttestation.load(path) is None


def test_a_missing_attestation_file_is_absent_rather_than_raising(tmp_path: Path) -> None:
    assert RuntimeAttestation.load(tmp_path / "nothing.json") is None
