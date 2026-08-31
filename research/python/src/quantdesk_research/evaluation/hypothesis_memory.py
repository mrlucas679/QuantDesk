"""Durable typed memory for rejected research hypotheses and next-action routing."""

import hashlib
import json
import sqlite3
from dataclasses import dataclass
from datetime import UTC, datetime
from enum import StrEnum
from pathlib import Path
from typing import Any


class FailureReason(StrEnum):
    REJECTED_COSTS = "RejectedCosts"
    INSUFFICIENT_TRADES = "InsufficientTrades"
    REGIME_INSTABILITY = "RegimeInstability"
    PARAMETER_FRAGILITY = "ParameterFragility"
    NO_RAW_EDGE = "NoRawEdge"
    TRANSFER_FAILURE = "TransferFailure"


NEXT_ACTION = {
    FailureReason.REJECTED_COSTS: "increase_horizon_or_reduce_turnover",
    FailureReason.INSUFFICIENT_TRADES: "broaden_assets_or_extend_data",
    FailureReason.REGIME_INSTABILITY: "test_preregistered_regime_conditioning",
    FailureReason.PARAMETER_FRAGILITY: "reject_parameter_neighborhood",
    FailureReason.NO_RAW_EDGE: "reject_hypothesis_family",
    FailureReason.TRANSFER_FAILURE: "mark_asset_specific",
}


@dataclass(frozen=True)
class RejectedHypothesis:
    hypothesis_id: str
    mechanism: str
    reason: FailureReason
    dataset_hash: str
    parameters: dict[str, Any]
    evidence: dict[str, Any]
    regime: str
    cost_scenario: str

    @property
    def fingerprint(self) -> str:
        """Bind the hypothesis neighborhood to evidence and economic assumptions."""
        return hashlib.sha256(
            json.dumps(
                {
                    "hypothesis_id": self.hypothesis_id,
                    "dataset_hash": self.dataset_hash,
                    "parameters": self.parameters,
                    "regime": self.regime,
                    "cost_scenario": self.cost_scenario,
                },
                separators=(",", ":"),
                sort_keys=True,
            ).encode("utf-8")
        ).hexdigest()


class HypothesisMemory:
    """Prevents an exhausted hypothesis neighborhood being silently rerun."""

    def __init__(self, database_path: Path) -> None:
        self._database_path = database_path
        database_path.parent.mkdir(parents=True, exist_ok=True)
        with sqlite3.connect(database_path) as connection:
            connection.execute(
                """
                CREATE TABLE IF NOT EXISTS rejected_hypotheses (
                    fingerprint TEXT PRIMARY KEY,
                    hypothesis_id TEXT NOT NULL,
                    mechanism TEXT NOT NULL,
                    reason TEXT NOT NULL,
                    dataset_hash TEXT NOT NULL,
                    parameters_json TEXT NOT NULL,
                    evidence_json TEXT NOT NULL,
                    regime TEXT NOT NULL,
                    cost_scenario TEXT NOT NULL,
                    next_action TEXT NOT NULL,
                    recorded_at TEXT NOT NULL
                )
                """
            )

    def record(self, rejection: RejectedHypothesis) -> bool:
        """Record a unique rejected neighborhood; return false for a duplicate."""
        with sqlite3.connect(self._database_path) as connection:
            cursor = connection.execute(
                """
                INSERT OR IGNORE INTO rejected_hypotheses (
                    fingerprint, hypothesis_id, mechanism, reason, dataset_hash,
                    parameters_json, evidence_json, regime, cost_scenario,
                    next_action, recorded_at
                ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                """,
                (
                    rejection.fingerprint,
                    rejection.hypothesis_id,
                    rejection.mechanism,
                    rejection.reason.value,
                    rejection.dataset_hash,
                    json.dumps(rejection.parameters, sort_keys=True),
                    json.dumps(rejection.evidence, sort_keys=True),
                    rejection.regime,
                    rejection.cost_scenario,
                    NEXT_ACTION[rejection.reason],
                    datetime.now(UTC).isoformat(),
                ),
            )
            return cursor.rowcount == 1

    def contains(self, rejection: RejectedHypothesis) -> bool:
        """Return whether the exact hypothesis neighborhood is already exhausted."""
        with sqlite3.connect(self._database_path) as connection:
            row = connection.execute(
                "SELECT 1 FROM rejected_hypotheses WHERE fingerprint = ?",
                (rejection.fingerprint,),
            ).fetchone()
            return row is not None

    def list_rejections(self) -> list[dict[str, Any]]:
        """Return typed rejection summaries for the next research decision."""
        with sqlite3.connect(self._database_path) as connection:
            connection.row_factory = sqlite3.Row
            rows = connection.execute(
                """
                SELECT hypothesis_id, reason, dataset_hash, regime,
                       cost_scenario, next_action, recorded_at
                FROM rejected_hypotheses ORDER BY recorded_at
                """
            ).fetchall()
            return [dict(row) for row in rows]


def classify_failure(
    *,
    gross_mean_bps: float,
    net_mean_bps: float,
    trade_count: int,
    minimum_trades: int,
    regime_sign_consistent: bool = True,
    parameter_stable: bool = True,
    transfer_only_failure: bool = False,
) -> FailureReason:
    """Route a failed result by its lowest-level economic or robustness cause."""
    if transfer_only_failure:
        return FailureReason.TRANSFER_FAILURE
    if trade_count < minimum_trades:
        return FailureReason.INSUFFICIENT_TRADES
    if gross_mean_bps <= 0:
        return FailureReason.NO_RAW_EDGE
    if net_mean_bps <= 0:
        return FailureReason.REJECTED_COSTS
    if not parameter_stable:
        return FailureReason.PARAMETER_FRAGILITY
    if not regime_sign_consistent:
        return FailureReason.REGIME_INSTABILITY
    return FailureReason.REGIME_INSTABILITY
