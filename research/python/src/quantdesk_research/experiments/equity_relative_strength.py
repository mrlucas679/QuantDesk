from __future__ import annotations

import argparse
import hashlib
import json
import math
from dataclasses import asdict, dataclass
from pathlib import Path
from statistics import NormalDist
from typing import Any, Literal, cast

import numpy as np
import pandas as pd  # type: ignore[import-untyped]
from numpy.typing import NDArray

from quantdesk_research.backtest.equity_costs import BASE_COST, STRESS_COST

SYMBOLS = ("SPY", "QQQ", "IWM", "DIA")
PRIOR_COMPARISONS = 20
MINIMUM_TRADES = 30
Phase = Literal["discovery", "validation", "holdout"]
JsonObject = dict[str, Any]


@dataclass(frozen=True)
class RelativeStrengthCandidate:
    """One fixed capital-flow persistence or reversal hypothesis."""

    name: str
    lookback_days: int
    holding_days: int
    select_strongest: bool
    require_positive_signal: bool
    maximum_volatility: float | None = None


@dataclass(frozen=True)
class RelativeStrengthEvaluation:
    """Costed, multiplicity-aware evidence for one chronological phase."""

    name: str
    phase: Phase
    passed: bool
    trade_count: int
    mean_net_bps: float
    stress_mean_net_bps: float
    lower_confidence_net_bps: float
    sharpe: float
    win_rate: float
    maximum_drawdown_bps: float
    data_hashes: tuple[str, ...]
    comparison_count: int
    gate_reasons: tuple[str, ...]


CANDIDATES = (
    RelativeStrengthCandidate("relative-momentum-20d-hold-5d", 20, 5, True, True),
    RelativeStrengthCandidate("relative-momentum-63d-hold-5d", 63, 5, True, True),
    RelativeStrengthCandidate("relative-momentum-63d-hold-10d", 63, 10, True, True),
    RelativeStrengthCandidate("relative-momentum-126d-hold-10d", 126, 10, True, True),
    RelativeStrengthCandidate(
        "low-vol-relative-momentum-63d-hold-5d", 63, 5, True, True, 0.22
    ),
    RelativeStrengthCandidate("cross-sectional-reversal-5d-hold-5d", 5, 5, False, False),
)


def evaluate_candidate(
    data_root: Path,
    candidate: RelativeStrengthCandidate,
    phase: Phase,
) -> RelativeStrengthEvaluation:
    """Evaluate a fixed cross-asset rule with causal next-session entry."""
    frame, hashes = load_daily_panel(data_root)
    returns = candidate_returns(frame, candidate)
    selected = chronological_phase(returns, phase)
    gross = selected.to_numpy(dtype=np.float64)
    net = gross - BASE_COST.round_trip_bps / 10_000
    stress = gross - STRESS_COST.round_trip_bps / 10_000
    comparison_count = PRIOR_COMPARISONS + len(CANDIDATES)
    alpha = 0.05 if phase == "discovery" else 0.05 / comparison_count
    lower = lower_mean_bound(net, alpha)
    standard_deviation = float(net.std(ddof=1)) if len(net) > 1 else 0.0
    annualization = math.sqrt(252 / candidate.holding_days)
    sharpe = float(net.mean()) / standard_deviation * annualization if standard_deviation else 0.0
    cumulative = np.cumsum(net) if len(net) else np.asarray([0.0])
    drawdown = cumulative - np.maximum.accumulate(cumulative)
    reasons = gate_reasons(net, stress, lower, sharpe, phase)
    return RelativeStrengthEvaluation(
        name=candidate.name,
        phase=phase,
        passed=not reasons,
        trade_count=len(net),
        mean_net_bps=finite_bps(float(net.mean()) if len(net) else float("-inf")),
        stress_mean_net_bps=finite_bps(float(stress.mean()) if len(stress) else float("-inf")),
        lower_confidence_net_bps=finite_bps(lower),
        sharpe=round(sharpe, 6),
        win_rate=round(float((net > 0).mean()), 6) if len(net) else 0.0,
        maximum_drawdown_bps=round(float(drawdown.min()) * 10_000, 6),
        data_hashes=hashes,
        comparison_count=comparison_count,
        gate_reasons=tuple(reasons),
    )


def load_daily_panel(data_root: Path) -> tuple[pd.DataFrame, tuple[str, ...]]:
    """Load hash-verified adjusted SIP daily bars into one aligned price panel."""
    frames: list[pd.DataFrame] = []
    hashes: list[str] = []
    for symbol in SYMBOLS:
        manifest_path = data_root / f"latest-{symbol.lower()}-1day-sip.manifest.json"
        manifest = cast(JsonObject, json.loads(manifest_path.read_text(encoding="utf-8")))
        if manifest.get("feed") != "sip" or manifest.get("adjustment") != "all":
            raise ValueError(f"Relative-strength research requires SIP/all: {manifest_path.name}.")
        data_path = data_root / str(manifest["data_file"])
        payload = data_path.read_bytes()
        digest = f"sha256:{hashlib.sha256(payload).hexdigest()}"
        if digest != manifest.get("sha256"):
            raise ValueError(f"Immutable dataset hash mismatch: {data_path.name}.")
        bars = json.loads(payload)
        if not isinstance(bars, list) or len(bars) != manifest.get("row_count"):
            raise ValueError(f"Immutable dataset row-count mismatch: {data_path.name}.")
        frame = pd.DataFrame(cast(list[JsonObject], bars))
        frame["date"] = pd.to_datetime(frame["t"], utc=True).dt.date
        frame["symbol"] = symbol
        frames.append(frame[["date", "symbol", "o", "c"]])
        hashes.append(digest)
    panel = pd.concat(frames, ignore_index=True).sort_values(["date", "symbol"])
    return panel, tuple(hashes)


def candidate_returns(
    panel: pd.DataFrame,
    candidate: RelativeStrengthCandidate,
) -> pd.Series:
    """Select one asset causally and enforce non-overlapping holding periods."""
    close = panel.pivot(index="date", columns="symbol", values="c").astype(float)
    open_price = panel.pivot(index="date", columns="symbol", values="o").astype(float)
    signal = close.pct_change(candidate.lookback_days)
    volatility = close.pct_change().rolling(20).std() * math.sqrt(252)
    future_return = close.shift(-candidate.holding_days) / open_price.shift(-1) - 1
    selected: list[float] = []
    next_eligible = 0
    for index in range(candidate.lookback_days, len(close) - candidate.holding_days):
        if index < next_eligible:
            continue
        row = signal.iloc[index].dropna()
        if row.empty:
            continue
        symbol = row.idxmax() if candidate.select_strongest else row.idxmin()
        value = float(row[symbol])
        if candidate.require_positive_signal and value <= 0:
            continue
        if candidate.maximum_volatility is not None:
            observed_volatility = float(volatility.iloc[index][symbol])
            if not math.isfinite(observed_volatility) or observed_volatility > candidate.maximum_volatility:
                continue
        realized = float(future_return.iloc[index][symbol])
        if math.isfinite(realized):
            selected.append(realized)
            next_eligible = index + candidate.holding_days
    return pd.Series(selected, dtype=float)


def chronological_phase(returns: pd.Series, phase: Phase) -> pd.Series:
    """Keep discovery, validation, and final holdout mutually exclusive."""
    first = len(returns) // 2
    second = first + len(returns) // 4
    if phase == "discovery":
        return returns.iloc[:first].reset_index(drop=True)
    if phase == "validation":
        return returns.iloc[first:second].reset_index(drop=True)
    return returns.iloc[second:].reset_index(drop=True)


def lower_mean_bound(values: NDArray[np.float64], alpha: float) -> float:
    """Return a one-sided normal lower confidence bound."""
    if len(values) < 2:
        return float("-inf")
    standard_error = float(values.std(ddof=1)) / math.sqrt(len(values))
    critical = NormalDist().inv_cdf(1 - alpha)
    return float(values.mean()) - critical * standard_error


def gate_reasons(
    net: NDArray[np.float64],
    stress: NDArray[np.float64],
    lower: float,
    sharpe: float,
    phase: Phase,
) -> list[str]:
    """Apply unchanged conservative gates; holdout adds stress and subwindow checks."""
    reasons: list[str] = []
    if len(net) < MINIMUM_TRADES:
        reasons.append(f"trade_count_below_{MINIMUM_TRADES}")
    if not len(net) or float(net.mean()) <= 0:
        reasons.append("base_expectancy_not_positive")
    if lower <= 0:
        reasons.append("confidence_lower_bound_not_positive")
    if sharpe <= 0.5:
        reasons.append("sharpe_not_above_0_5")
    if phase == "holdout" and (not len(stress) or float(stress.mean()) <= 0):
        reasons.append("stress_expectancy_not_positive")
    if phase == "holdout" and len(net) >= 2:
        midpoint = len(net) // 2
        if float(net[:midpoint].mean()) <= 0 or float(net[midpoint:].mean()) <= 0:
            reasons.append("holdout_subwindow_instability")
    return reasons


def finite_bps(value: float) -> float:
    return round(value * 10_000, 6) if math.isfinite(value) else -1_000_000


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--data-root", type=Path, required=True)
    parser.add_argument("--phase", choices=("discovery", "validation", "holdout"), required=True)
    parser.add_argument("--candidate", choices=[item.name for item in CANDIDATES])
    arguments = parser.parse_args()
    candidates = CANDIDATES if arguments.candidate is None else tuple(
        item for item in CANDIDATES if item.name == arguments.candidate
    )
    results = [evaluate_candidate(arguments.data_root, item, arguments.phase) for item in candidates]
    print(json.dumps([asdict(result) for result in results], sort_keys=True))
    return 0 if any(result.passed for result in results) else 1


if __name__ == "__main__":
    raise SystemExit(main())
