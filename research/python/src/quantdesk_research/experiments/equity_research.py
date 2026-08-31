from __future__ import annotations

import argparse
import hashlib
import json
import math
import sys
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Any, Literal, cast

import numpy as np
import pandas as pd  # type: ignore[import-untyped]
from numpy.typing import NDArray
from scipy import stats  # type: ignore[import-untyped]

from quantdesk_research.backtest.equity_costs import (
    BASE_COST,
    COST_SCENARIOS,
)

SYMBOLS = ("SPY", "QQQ", "IWM", "DIA")
TRIAL_COUNT = 20
MIN_TRADES = 30
BONFERRONI_ALPHA = 0.05 / TRIAL_COUNT
HOLDOUT_FRACTION = 0.25

JsonObject = dict[str, Any]
Phase = Literal["validation", "holdout"]


@dataclass(frozen=True)
class Candidate:
    """One preregistered, causal equity hypothesis."""

    number: int
    slug: str
    description: str
    family: Literal["daily", "intraday"]


@dataclass(frozen=True)
class Evaluation:
    """Mechanical evidence for one candidate and one chronological phase."""

    passed: bool
    score: float
    candidate: int
    slug: str
    phase: Phase
    trade_count: int
    base_mean_net_bps: float
    base_lower_confidence_bps: float
    base_win_rate: float
    stress_mean_net_bps: float
    severe_mean_net_bps: float
    max_drawdown_bps: float
    worst_trade_bps: float
    first_half_mean_bps: float
    second_half_mean_bps: float
    selection_alpha: float
    data_hashes: tuple[str, ...]
    gate_reasons: tuple[str, ...]


CANDIDATES = (
    Candidate(1, "daily-1d-reversal", "Buy after a prior close-to-close loss below -0.50%.", "daily"),
    Candidate(2, "daily-3d-reversal", "Buy after a prior three-day loss below -1.00%.", "daily"),
    Candidate(3, "daily-5d-reversal", "Buy after a prior five-day loss below -1.50%.", "daily"),
    Candidate(4, "daily-5d-momentum", "Buy after prior five-day momentum above 1.50%.", "daily"),
    Candidate(5, "daily-20d-momentum", "Buy after prior 20-day momentum above 3.00%.", "daily"),
    Candidate(6, "daily-20-100-trend", "Buy when the prior 20-day mean exceeds the 100-day mean.", "daily"),
    Candidate(7, "daily-50-200-trend", "Buy when the prior 50-day mean exceeds the 200-day mean.", "daily"),
    Candidate(8, "daily-low-vol-trend", "Buy positive 20-day trend below 20% annualized volatility.", "daily"),
    Candidate(9, "daily-high-vol-reversal", "Buy a prior -0.75% loss above 20% annualized volatility.", "daily"),
    Candidate(10, "daily-volume-breakout", "Buy a prior 20-day high with elevated volume.", "daily"),
    Candidate(11, "daily-monday", "Buy the regular session on Mondays.", "daily"),
    Candidate(12, "daily-friday", "Buy the regular session on Fridays.", "daily"),
    Candidate(13, "open-30m-continuation", "Buy after a positive first 30 minutes.", "intraday"),
    Candidate(14, "open-30m-reversal", "Buy after a first-30-minute loss below -0.20%.", "intraday"),
    Candidate(15, "open-60m-continuation", "Buy after a positive first 60 minutes.", "intraday"),
    Candidate(16, "open-60m-reversal", "Buy after a first-60-minute loss below -0.30%.", "intraday"),
    Candidate(17, "gap-continuation", "Buy after an overnight gap above 0.20%, from 09:35.", "intraday"),
    Candidate(18, "gap-reversal", "Buy after an overnight gap below -0.30%, from 09:35.", "intraday"),
    Candidate(19, "morning-vwap-reversal", "Buy after price trails two-hour VWAP by 0.20%.", "intraday"),
    Candidate(20, "last-hour-momentum", "Buy the last 30 minutes after positive intraday momentum.", "intraday"),
)


def load_research_frames(data_root: Path) -> tuple[pd.DataFrame, pd.DataFrame, tuple[str, ...]]:
    """Load hash-verified daily and regular-session intraday SIP datasets."""
    daily_frames: list[pd.DataFrame] = []
    intraday_frames: list[pd.DataFrame] = []
    hashes: list[str] = []
    for symbol in SYMBOLS:
        daily_bars, daily_hash = _load_dataset(data_root, symbol, "1day")
        intraday_bars, intraday_hash = _load_dataset(data_root, symbol, "5min")
        daily_frames.append(build_daily_features(daily_bars, symbol))
        intraday_frames.append(build_intraday_features(intraday_bars, symbol))
        hashes.extend((daily_hash, intraday_hash))
    daily = pd.concat(daily_frames, ignore_index=True)
    intraday = pd.concat(intraday_frames, ignore_index=True)
    return daily, intraday, tuple(hashes)


def build_daily_features(bars: list[JsonObject], symbol: str) -> pd.DataFrame:
    """Build daily features known before the current regular-session open."""
    frame = pd.DataFrame(bars).sort_values("t").drop_duplicates("t").reset_index(drop=True)
    frame["date"] = pd.to_datetime(frame["t"], utc=True).dt.date
    frame["symbol"] = symbol
    close = frame["c"].astype(float)
    volume = frame["v"].astype(float)
    daily_return = close.pct_change()
    frame["prior_return_1"] = daily_return.shift(1)
    for window in (3, 5, 20):
        frame[f"prior_return_{window}"] = close.pct_change(window).shift(1)
    for window in (20, 50, 100, 200):
        frame[f"prior_sma_{window}"] = close.rolling(window).mean().shift(1)
    frame["prior_volatility_20"] = daily_return.rolling(20).std().shift(1) * math.sqrt(252)
    frame["prior_volume_ratio_20"] = volume.shift(1) / volume.rolling(20).median().shift(1)
    frame["prior_breakout_20"] = close.shift(1) > close.shift(2).rolling(20).max()
    frame["gross_return"] = frame["c"].astype(float) / frame["o"].astype(float) - 1
    return frame.replace([np.inf, -np.inf], np.nan)


def build_intraday_features(bars: list[JsonObject], symbol: str) -> pd.DataFrame:
    """Aggregate complete 5-minute regular sessions into causal decision rows."""
    frame = pd.DataFrame(bars).sort_values("t").drop_duplicates("t").reset_index(drop=True)
    timestamp = pd.to_datetime(frame["t"], utc=True).dt.tz_convert("America/New_York")
    frame["local_timestamp"] = timestamp
    frame["date"] = timestamp.dt.date
    frame["local_time"] = timestamp.dt.strftime("%H:%M")
    regular = frame[(frame["local_time"] >= "09:30") & (frame["local_time"] <= "15:55")]
    sessions: list[dict[str, Any]] = []
    prior_close: float | None = None
    for session_date, rows in regular.groupby("date", sort=True):
        row = _aggregate_complete_session(rows.reset_index(drop=True), symbol, session_date, prior_close)
        if row is not None:
            sessions.append(row)
            prior_close = float(row["close"])
    return pd.DataFrame(sessions)


def _aggregate_complete_session(
    rows: pd.DataFrame,
    symbol: str,
    session_date: object,
    prior_close: float | None,
) -> dict[str, Any] | None:
    if len(rows) != 78 or rows.iloc[0]["local_time"] != "09:30":
        return None
    if rows.iloc[-1]["local_time"] != "15:55":
        return None
    open_price = float(rows.iloc[0]["o"])
    close_price = float(rows.iloc[-1]["c"])
    volume = rows.iloc[:24]["v"].astype(float).to_numpy()
    vwap = rows.iloc[:24]["vw"].astype(float).to_numpy()
    morning_vwap = float(np.average(vwap, weights=volume)) if volume.sum() > 0 else math.nan
    return {
        "date": session_date,
        "symbol": symbol,
        "close": close_price,
        "open_30_return": float(rows.iloc[5]["c"]) / open_price - 1,
        "after_30_return": close_price / float(rows.iloc[6]["o"]) - 1,
        "open_60_return": float(rows.iloc[11]["c"]) / open_price - 1,
        "after_60_return": close_price / float(rows.iloc[12]["o"]) - 1,
        "gap_return": open_price / prior_close - 1 if prior_close else math.nan,
        "after_5_return": close_price / float(rows.iloc[1]["o"]) - 1,
        "morning_vwap_distance": float(rows.iloc[23]["c"]) / morning_vwap - 1,
        "after_2h_return": close_price / float(rows.iloc[24]["o"]) - 1,
        "intraday_to_1530": float(rows.iloc[71]["c"]) / open_price - 1,
        "last_30_return": close_price / float(rows.iloc[72]["o"]) - 1,
    }


def candidate_returns(
    candidate_number: int,
    daily: pd.DataFrame,
    intraday: pd.DataFrame,
) -> pd.DataFrame:
    """Return one equal-weight portfolio observation per active session."""
    candidate = get_candidate(candidate_number)
    rows = _daily_candidate_rows(candidate_number, daily) if candidate.family == "daily" else _intraday_candidate_rows(candidate_number, intraday)
    selected = rows.loc[rows["signal"].fillna(False), ["date", "symbol", "gross_return"]]
    return (
        selected.groupby("date", as_index=False)["gross_return"]
        .mean()
        .sort_values("date")
        .reset_index(drop=True)
    )


def _daily_candidate_rows(number: int, frame: pd.DataFrame) -> pd.DataFrame:
    result = frame.copy()
    signals: dict[int, pd.Series] = {
        1: result["prior_return_1"] < -0.005,
        2: result["prior_return_3"] < -0.010,
        3: result["prior_return_5"] < -0.015,
        4: result["prior_return_5"] > 0.015,
        5: result["prior_return_20"] > 0.030,
        6: result["prior_sma_20"] > result["prior_sma_100"],
        7: result["prior_sma_50"] > result["prior_sma_200"],
        8: (result["prior_return_20"] > 0) & (result["prior_volatility_20"] < 0.20),
        9: (result["prior_return_1"] < -0.0075) & (result["prior_volatility_20"] > 0.20),
        10: result["prior_breakout_20"] & (result["prior_volume_ratio_20"] > 1.25),
        11: pd.to_datetime(result["date"]).dt.dayofweek == 0,
        12: pd.to_datetime(result["date"]).dt.dayofweek == 4,
    }
    result["signal"] = signals[number]
    return result


def _intraday_candidate_rows(number: int, frame: pd.DataFrame) -> pd.DataFrame:
    result = frame.copy()
    rules: dict[int, tuple[pd.Series, str]] = {
        13: (result["open_30_return"] > 0, "after_30_return"),
        14: (result["open_30_return"] < -0.002, "after_30_return"),
        15: (result["open_60_return"] > 0, "after_60_return"),
        16: (result["open_60_return"] < -0.003, "after_60_return"),
        17: (result["gap_return"] > 0.002, "after_5_return"),
        18: (result["gap_return"] < -0.003, "after_5_return"),
        19: (result["morning_vwap_distance"] < -0.002, "after_2h_return"),
        20: (result["intraday_to_1530"] > 0, "last_30_return"),
    }
    signal, return_column = rules[number]
    result["signal"] = signal
    result["gross_return"] = result[return_column]
    return result


def chronological_phase(returns: pd.DataFrame, phase: Phase) -> pd.DataFrame:
    """Expose validation or final holdout while preserving an untouched first half."""
    if returns.empty:
        return returns.copy()
    dates = np.asarray(sorted(returns["date"].unique()))
    validation_start = dates[int(len(dates) * 0.50)]
    holdout_start = dates[int(len(dates) * (1 - HOLDOUT_FRACTION))]
    if phase == "validation":
        mask = (returns["date"] >= validation_start) & (returns["date"] < holdout_start)
    else:
        mask = returns["date"] >= holdout_start
    return returns.loc[mask].reset_index(drop=True)


def evaluate_candidate(
    candidate_number: int,
    data_root: Path,
    phase: Phase = "validation",
) -> Evaluation:
    """Evaluate one preregistered candidate with no parameter fitting."""
    daily, intraday, hashes = load_research_frames(data_root)
    candidate = get_candidate(candidate_number)
    observations = chronological_phase(candidate_returns(candidate.number, daily, intraday), phase)
    gross = observations["gross_return"].to_numpy(dtype=np.float64)
    base = gross - BASE_COST.round_trip_bps / 10_000
    alpha = BONFERRONI_ALPHA if phase == "validation" else 0.05
    lower = lower_mean_bound(base, alpha)
    means = {
        scenario.name: _safe_mean(gross - scenario.round_trip_bps / 10_000)
        for scenario in COST_SCENARIOS
    }
    split = len(base) // 2
    first_half = _safe_mean(base[:split])
    second_half = _safe_mean(base[split:])
    cumulative = np.cumsum(base) if len(base) else np.array([0.0])
    drawdown = cumulative - np.maximum.accumulate(cumulative)
    reasons = _gate_reasons(base, lower, means, first_half, second_half, phase)
    score = lower * 10_000 if math.isfinite(lower) else -1_000_000.0
    return Evaluation(
        passed=not reasons,
        score=round(score, 6),
        candidate=candidate.number,
        slug=candidate.slug,
        phase=phase,
        trade_count=len(base),
        base_mean_net_bps=round(means["BASE"] * 10_000, 6),
        base_lower_confidence_bps=round(score, 6),
        base_win_rate=round(float((base > 0).mean()), 6) if len(base) else 0.0,
        stress_mean_net_bps=round(means["STRESS"] * 10_000, 6),
        severe_mean_net_bps=round(means["SEVERE"] * 10_000, 6),
        max_drawdown_bps=round(float(drawdown.min()) * 10_000, 6),
        worst_trade_bps=round(float(base.min()) * 10_000, 6) if len(base) else -1_000_000,
        first_half_mean_bps=round(first_half * 10_000, 6),
        second_half_mean_bps=round(second_half * 10_000, 6),
        selection_alpha=alpha,
        data_hashes=hashes,
        gate_reasons=tuple(reasons),
    )


def _gate_reasons(
    base: NDArray[np.float64],
    lower: float,
    means: dict[str, float],
    first_half: float,
    second_half: float,
    phase: Phase,
) -> list[str]:
    reasons: list[str] = []
    if len(base) < MIN_TRADES:
        reasons.append(f"trade_count_below_{MIN_TRADES}")
    if means["BASE"] <= 0:
        reasons.append("base_expectancy_not_positive")
    if lower <= 0:
        reasons.append("confidence_lower_bound_not_positive")
    if phase == "holdout" and means["STRESS"] <= 0:
        reasons.append("stress_expectancy_not_positive")
    if phase == "holdout" and (first_half <= 0 or second_half <= 0):
        reasons.append("holdout_subwindow_instability")
    if len(base) and float(base.min()) <= -0.05:
        reasons.append("single_trade_loss_exceeds_five_percent")
    return reasons


def lower_mean_bound(values: NDArray[np.float64], alpha: float) -> float:
    """Return a one-sided Student-t lower confidence bound for the mean."""
    if len(values) < 2:
        return float("-inf")
    standard_error = float(values.std(ddof=1)) / math.sqrt(len(values))
    critical = float(stats.t.ppf(1 - alpha, df=len(values) - 1))
    return float(values.mean()) - critical * standard_error


def get_candidate(number: int) -> Candidate:
    """Resolve a registered candidate or reject the request."""
    if not 1 <= number <= len(CANDIDATES):
        raise ValueError(f"Candidate must be between 1 and {len(CANDIDATES)}.")
    return CANDIDATES[number - 1]


def _load_dataset(data_root: Path, symbol: str, timeframe_slug: str) -> tuple[list[JsonObject], str]:
    manifest_path = data_root / f"latest-{symbol.lower()}-{timeframe_slug}-sip.manifest.json"
    manifest = cast(JsonObject, json.loads(manifest_path.read_text(encoding="utf-8")))
    if manifest.get("feed") != "sip" or manifest.get("adjustment") != "all":
        raise ValueError(f"Research requires SIP/all data: {manifest_path.name}.")
    data_path = data_root / str(manifest["data_file"])
    payload = data_path.read_bytes()
    actual_hash = f"sha256:{hashlib.sha256(payload).hexdigest()}"
    if actual_hash != manifest.get("sha256"):
        raise ValueError(f"Immutable dataset hash mismatch: {data_path.name}.")
    bars = json.loads(payload)
    if not isinstance(bars, list) or len(bars) != manifest.get("row_count"):
        raise ValueError(f"Immutable dataset row-count mismatch: {data_path.name}.")
    return cast(list[JsonObject], bars), actual_hash


def _safe_mean(values: NDArray[np.float64]) -> float:
    return float(values.mean()) if len(values) else float("-inf")


def main() -> int:
    parser = argparse.ArgumentParser(description="Evaluate one preregistered equity hypothesis.")
    parser.add_argument("--data-root", type=Path, required=True)
    parser.add_argument("--candidate", type=int, required=True)
    parser.add_argument("--phase", choices=("validation", "holdout"), default="validation")
    arguments = parser.parse_args()
    evaluation = evaluate_candidate(arguments.candidate, arguments.data_root, arguments.phase)
    payload = asdict(evaluation)
    payload["pass"] = payload.pop("passed")
    print(json.dumps(payload, sort_keys=True))
    return 0


if __name__ == "__main__":
    sys.exit(main())
