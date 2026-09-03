"""Real evaluators for the R-gates that promotion requires.

Why this exists
---------------
``ContractPublisher`` refuses an artifact that does not carry passing evidence for R0, R1, R2, R3,
R4, R5, R6, R7, R11 and R12. Both promotion paths declared a subset and an empty evidence map --
``validation_gates=["R0", "R1", "R2", "R4"]`` with ``validation_evidence={}`` in the directional
path, and the campaign path never built an artifact at all. Neither could ever have published.
Nothing noticed, because no candidate has yet passed the statistical gates that come first, so the
last rung of the ladder has never carried weight.

The failure mode this is written against
----------------------------------------
The tempting fix is to declare the ten gates and mark them passed, which turns a governance control
into a form to fill in and would put money behind whichever strategy first cleared a t-test. So
every gate here is a function of something measured, and a gate whose evidence does not exist
returns ``passed=False`` with the reason in ``details``. Publication then refuses by name -- "R5 did
not pass: no runtime attestation" -- rather than refusing because a dict was empty.

Wiring this in therefore promotes nothing today, and is not meant to. It makes the refusals true
statements about evidence instead of an accident of unfinished code.

The split between what research knows and what the runtime knows
----------------------------------------------------------------
R0 through R7 are properties of the candidate and its backtest, and research measures them. R11
(execution safety) and R12 (operational runtime) are properties of the execution plane -- restart
safe client order ids, reconciliation, bounded queues, measured p99 latency, a verified paper
endpoint. Research cannot observe any of that, and asserting it from here would be exactly the
fabrication described above. They are answered from a runtime attestation the C# plane writes into
the shared research volume, and they fail closed when it is absent, stale, or reports a fault.
"""

from __future__ import annotations

import json
from collections.abc import Callable
from dataclasses import dataclass
from datetime import UTC, datetime, timedelta
from pathlib import Path
from typing import Any

from quantdesk_research.contracts.model_artifact import (
    EvidenceProfile,
    ValidationGateEvidence,
)

REQUIRED_GATES: tuple[str, ...] = ("R0", "R1", "R2", "R3", "R4", "R5", "R6", "R7", "R11", "R12")

#: How old a runtime attestation may be and still describe the system that is running now.
MAXIMUM_ATTESTATION_AGE = timedelta(hours=6)

#: Section 20.3 keeps a promoted candidate's drawdown bounded, in basis points of traded notional.
MAXIMUM_DRAWDOWN_BPS = 2_000.0

#: A candidate whose alpha decays faster than the runtime's own worst-case path cannot be acted on
#: in time, whatever the backtest says. Doubling the measured path leaves room for the fill.
LATENCY_SAFETY_MARGIN = 2.0

GateFactory = Callable[[str, bool, dict[str, Any]], ValidationGateEvidence]


@dataclass(frozen=True)
class CandidateMeasurements:
    """What research actually measured about one candidate.

    Every field is a number some evaluator produced, not a claim entered by hand. ``None`` means the
    measurement was not taken, which fails its gate rather than reading as a zero.
    """

    strategy_name: str
    #: Out-of-sample net edge after round-trip costs, at the lower confidence bound.
    lower_confidence_net_bps: float
    #: The same figure for the horizon-matched simple baseline the candidate must beat.
    baseline_lower_confidence_net_bps: float | None
    trade_count: int
    sharpe: float
    maximum_drawdown_bps: float
    round_trip_cost_bps: float
    #: How long the signal stays useful, compared against the runtime's measured decision path.
    alpha_life_minutes: float
    #: Whether labels overlapping the evaluation window were purged and embargoed.
    labels_purged_and_embargoed: bool
    #: Whether the fit ended strictly before the earliest prediction it is scored on.
    fit_ends_before_prediction: bool
    #: How many independent configurations were evaluated, for trial accounting.
    trials_evaluated: int
    #: Distinct seeds, or 1 for a deterministic rule that has no seed to vary.
    seeds_evaluated: int
    #: How many separated folds it survived, rather than one continuous stretch.
    walk_forward_folds: int
    #: How the result was produced, in section 22's vocabulary.
    evidence_class: str
    dataset_hash: str


@dataclass(frozen=True)
class RuntimeAttestation:
    """What the execution plane says about itself, read rather than assumed."""

    attested_at: datetime
    deterministic_client_order_ids: bool
    ambiguous_submit_resolves_unknown: bool
    reservation_before_submit: bool
    reconciliation_healthy: bool
    pending_order_invalidation: bool
    bounded_queues: bool
    no_reconnect_leak: bool
    paper_endpoint_verified: bool
    decision_path_p99_milliseconds: float | None
    data_age_p99_milliseconds: float | None
    replay_trace_hash: str | None

    @classmethod
    def load(cls, path: Path) -> RuntimeAttestation | None:
        """Read the attestation, or nothing when it is missing or unreadable.

        Returning ``None`` rather than raising keeps a missing attestation a failed gate instead of
        a crashed worker. It is never read as a pass.
        """
        try:
            payload: dict[str, Any] = json.loads(path.read_text(encoding="utf-8"))
            return cls(
                attested_at=datetime.fromisoformat(str(payload["attestedAt"])),
                deterministic_client_order_ids=bool(payload["deterministicClientOrderIds"]),
                ambiguous_submit_resolves_unknown=bool(payload["ambiguousSubmitResolvesUnknown"]),
                reservation_before_submit=bool(payload["reservationBeforeSubmit"]),
                reconciliation_healthy=bool(payload["reconciliationHealthy"]),
                pending_order_invalidation=bool(payload["pendingOrderInvalidation"]),
                bounded_queues=bool(payload["boundedQueues"]),
                no_reconnect_leak=bool(payload["noReconnectLeak"]),
                paper_endpoint_verified=bool(payload["paperEndpointVerified"]),
                decision_path_p99_milliseconds=_optional_float(
                    payload.get("decisionPathP99Milliseconds")
                ),
                data_age_p99_milliseconds=_optional_float(payload.get("dataAgeP99Milliseconds")),
                replay_trace_hash=_optional_str(payload.get("replayTraceHash")),
            )
        except (OSError, KeyError, TypeError, ValueError):
            return None


def _optional_float(value: Any) -> float | None:
    return None if value is None else float(value)


def _optional_str(value: Any) -> str | None:
    text = None if value is None else str(value).strip()
    return text or None


def evaluate_required_gates(
    measurements: CandidateMeasurements,
    evidence_profile: EvidenceProfile,
    attestation: RuntimeAttestation | None,
    evidence_id_prefix: str,
) -> dict[str, ValidationGateEvidence]:
    """Evaluate every gate promotion requires, passing only what the evidence supports."""
    evaluated_at = datetime.now(UTC)

    def gate(gate_id: str, passed: bool, details: dict[str, Any]) -> ValidationGateEvidence:
        return ValidationGateEvidence(
            gate_id=gate_id,
            passed=passed,
            evidence_ids=[f"{evidence_id_prefix}:{gate_id}"],
            evaluated_at=evaluated_at,
            details=details,
        )

    return {
        "R0": _evidence_identity(gate, evidence_profile),
        "R1": _point_in_time(gate, measurements),
        "R2": _simple_baseline(gate, measurements),
        "R3": _robustness(gate, measurements),
        "R4": _implementability(gate, measurements),
        "R5": _tradability(gate, measurements, attestation),
        "R6": _simulation_honesty(gate, measurements),
        "R7": _portfolio_tail(gate, measurements),
        "R11": _execution_safety(gate, attestation),
        "R12": _operational_runtime(gate, attestation),
    }


def failing_gates(evidence: dict[str, ValidationGateEvidence]) -> list[str]:
    """Which required gates are absent or did not pass, so a refusal can name them."""
    return sorted(
        gate_id
        for gate_id in REQUIRED_GATES
        if gate_id not in evidence or not evidence[gate_id].passed
    )


def describe_failures(evidence: dict[str, ValidationGateEvidence]) -> str:
    """A one-line account of why promotion was refused, naming each gate and its reason."""
    parts: list[str] = []
    for gate_id in failing_gates(evidence):
        found = evidence.get(gate_id)
        reason = None if found is None else found.details.get("reason")
        parts.append(f"{gate_id}({reason})" if reason else gate_id)
    return ", ".join(parts)


# ------------------------------------------------------------------ the candidate's own evidence


def _evidence_identity(gate: GateFactory, profile: EvidenceProfile) -> ValidationGateEvidence:
    """R0: the hypothesis, its counter-hypothesis, and where the evidence came from."""
    has_hypothesis = bool(profile.economic_hypothesis.strip())
    has_counter = bool(profile.counter_hypothesis.strip())
    has_evidence = bool(profile.primary_evidence_ids) and all(
        item.strip() for item in profile.primary_evidence_ids
    )
    transferable = profile.transfer_grade in {"A_Direct", "B_Close"}
    return gate(
        "R0",
        has_hypothesis and has_counter and has_evidence and transferable,
        {
            "economic_hypothesis_present": has_hypothesis,
            "counter_hypothesis_present": has_counter,
            "primary_evidence_present": has_evidence,
            "transfer_grade": profile.transfer_grade,
        },
    )


def _point_in_time(gate: GateFactory, m: CandidateMeasurements) -> ValidationGateEvidence:
    """R1: nothing the model saw postdates the moment it predicted."""
    return gate(
        "R1",
        m.fit_ends_before_prediction and m.labels_purged_and_embargoed,
        {
            "fit_ends_before_prediction": m.fit_ends_before_prediction,
            "labels_purged_and_embargoed": m.labels_purged_and_embargoed,
            "dataset_hash": m.dataset_hash,
        },
    )


def _simple_baseline(gate: GateFactory, m: CandidateMeasurements) -> ValidationGateEvidence:
    """R2: beating a horizon-matched simple baseline economically, not merely statistically."""
    baseline = m.baseline_lower_confidence_net_bps
    passed = baseline is not None and m.lower_confidence_net_bps > baseline
    return gate(
        "R2",
        passed,
        {
            "candidate_lower_confidence_net_bps": m.lower_confidence_net_bps,
            "baseline_lower_confidence_net_bps": baseline,
            "reason": "no baseline measured" if baseline is None else None,
        },
    )


def _robustness(gate: GateFactory, m: CandidateMeasurements) -> ValidationGateEvidence:
    """R3: a distribution of results rather than one lucky run.

    A deterministic rule has no seed to vary, so one seed satisfies that clause -- but it still has
    to survive separated folds, and its trial count still has to be accounted for.
    """
    enough_folds = m.walk_forward_folds >= 2
    accounted = m.trials_evaluated >= 1
    enough_seeds = m.seeds_evaluated >= 1
    return gate(
        "R3",
        enough_folds and accounted and enough_seeds,
        {
            "walk_forward_folds": m.walk_forward_folds,
            "trials_evaluated": m.trials_evaluated,
            "seeds_evaluated": m.seeds_evaluated,
        },
    )


def _implementability(gate: GateFactory, m: CandidateMeasurements) -> ValidationGateEvidence:
    """R4: edge that survives the round trip, at the lower bound rather than at the mean."""
    return gate(
        "R4",
        m.lower_confidence_net_bps > 0.0,
        {
            "lower_confidence_net_bps": m.lower_confidence_net_bps,
            "round_trip_cost_bps": m.round_trip_cost_bps,
            "trade_count": m.trade_count,
        },
    )


def _tradability(
    gate: GateFactory, m: CandidateMeasurements, attestation: RuntimeAttestation | None
) -> ValidationGateEvidence:
    """R5: the alpha outlives the path between seeing the data and having an order accepted."""
    if attestation is None:
        return gate("R5", False, {"reason": "no runtime attestation"})

    decision = attestation.decision_path_p99_milliseconds
    data_age = attestation.data_age_p99_milliseconds
    if decision is None or data_age is None:
        return gate(
            "R5", False, {"reason": "runtime has not measured its decision path or data age"}
        )

    budget_minutes = (decision + data_age) / 60_000.0 * LATENCY_SAFETY_MARGIN
    return gate(
        "R5",
        m.alpha_life_minutes > budget_minutes,
        {
            "alpha_life_minutes": m.alpha_life_minutes,
            "required_minutes": budget_minutes,
            "decision_path_p99_ms": decision,
            "data_age_p99_ms": data_age,
        },
    )


def _simulation_honesty(gate: GateFactory, m: CandidateMeasurements) -> ValidationGateEvidence:
    """R6: the result says how it was produced."""
    known = {"PassiveHistoricalReplay", "CounterfactualOrderBook", "BrokerPaper"}
    return gate("R6", m.evidence_class in known, {"evidence_class": m.evidence_class})


def _portfolio_tail(gate: GateFactory, m: CandidateMeasurements) -> ValidationGateEvidence:
    """R7: the candidate survives its own worst stretch."""
    # Magnitude, because the two evaluators disagree on the sign: the rolling experiment reports a
    # drawdown as a positive depth, the strategy cohort as the negative trough of a cumulative sum.
    # Comparing a signed value against an upper bound silently passes every candidate from one of
    # them, including the ruinous ones.
    depth = abs(m.maximum_drawdown_bps)
    bounded = depth <= MAXIMUM_DRAWDOWN_BPS
    return gate(
        "R7",
        bounded and m.sharpe > 0.0,
        {
            "maximum_drawdown_bps": depth,
            "maximum_permitted_bps": MAXIMUM_DRAWDOWN_BPS,
            "sharpe": m.sharpe,
        },
    )


# ------------------------------------------------------- what only the execution plane can answer


def _attestation_is_current(attestation: RuntimeAttestation) -> bool:
    age = datetime.now(UTC) - attestation.attested_at
    return timedelta(0) <= age <= MAXIMUM_ATTESTATION_AGE


def _stale(gate: GateFactory, gate_id: str, attestation: RuntimeAttestation) -> ValidationGateEvidence:
    return gate(
        gate_id,
        False,
        {
            "reason": "runtime attestation is stale",
            "attested_at": attestation.attested_at.isoformat(),
        },
    )


def _execution_safety(
    gate: GateFactory, attestation: RuntimeAttestation | None
) -> ValidationGateEvidence:
    """R11: the execution plane's own safety properties, as it reports them."""
    if attestation is None:
        return gate("R11", False, {"reason": "no runtime attestation"})
    if not _attestation_is_current(attestation):
        return _stale(gate, "R11", attestation)

    checks: dict[str, Any] = {
        "deterministic_client_order_ids": attestation.deterministic_client_order_ids,
        "ambiguous_submit_resolves_unknown": attestation.ambiguous_submit_resolves_unknown,
        "reservation_before_submit": attestation.reservation_before_submit,
        "reconciliation_healthy": attestation.reconciliation_healthy,
        "pending_order_invalidation": attestation.pending_order_invalidation,
    }
    return gate("R11", all(checks.values()), checks)


def _operational_runtime(
    gate: GateFactory, attestation: RuntimeAttestation | None
) -> ValidationGateEvidence:
    """R12: the runtime is measured, bounded, and pointed at the paper endpoint."""
    if attestation is None:
        return gate("R12", False, {"reason": "no runtime attestation"})
    if not _attestation_is_current(attestation):
        return _stale(gate, "R12", attestation)

    checks: dict[str, Any] = {
        "bounded_queues": attestation.bounded_queues,
        "no_reconnect_leak": attestation.no_reconnect_leak,
        "paper_endpoint_verified": attestation.paper_endpoint_verified,
        "latency_measured": attestation.decision_path_p99_milliseconds is not None,
        "alpha_age_measured": attestation.data_age_p99_milliseconds is not None,
        "replay_reproduced": attestation.replay_trace_hash is not None,
    }
    return gate("R12", all(checks.values()), checks)
