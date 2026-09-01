import argparse
import hashlib
import json
import math
import sys
from dataclasses import asdict, dataclass
from datetime import UTC, datetime
from pathlib import Path
from statistics import NormalDist
from typing import Any
from uuid import uuid4

import lightgbm as lgb
import numpy as np
import pandas as pd  # type: ignore[import-untyped]  # pandas-stubs is not installed
from numpy.typing import NDArray

from quantdesk_research.backtest.equity_costs import CRYPTO_TAKER_ROUND_TRIP_BPS_MEASURED
from quantdesk_research.backtest.realised_costs import resolve_round_trip_bps
from quantdesk_research.contracts.feature_schema import FeatureSchema
from quantdesk_research.contracts.forecast import Forecast, ForecastUncertainty
from quantdesk_research.contracts.model_artifact import (
    EvidenceProfile,
    ExitPolicyDefinition,
    ModelArtifact,
    StrategyDefinition,
)
from quantdesk_research.data.manifest_keys import (
    require_manifest_value,
)
from quantdesk_research.evaluation.trial_ledger import TrialLedger
from quantdesk_research.models.contract_publication import ContractPublisher
from quantdesk_research.models.model_registry import ModelRegistry

FEATURE_NAMES = (
    "return_1",
    "return_3",
    "return_12",
    "return_48",
    "volatility_12",
    "volatility_48",
    "range_fraction",
    "body_fraction",
    "volume_z_48",
    "vwap_distance",
    "hour_sin",
    "hour_cos",
)


def annual_periods(timeframe: str) -> int:
    """Return conservative annual bar counts for the supported continuous crypto timeframes."""
    normalized = timeframe.lower()
    if normalized == "5min":
        return 365 * 24 * 12
    if normalized == "1day":
        return 365
    raise ValueError(f"Unsupported research timeframe: {timeframe}")


@dataclass(frozen=True)
class DirectionEvaluation:
    passed: bool
    score: float
    calibration_threshold_bps: float
    test_trade_count: int
    test_mean_net_bps: float
    test_lower_confidence_net_bps: float
    test_win_rate: float
    test_sharpe: float
    test_max_drawdown_bps: float
    dataset_hash: str


def build_feature_frame(bars: list[dict[str, Any]], horizon_bars: int = 3) -> pd.DataFrame:
    """Build strictly backward-looking features while retaining the latest actionable row."""
    frame = pd.DataFrame(bars).sort_values("t").drop_duplicates("t").reset_index(drop=True)
    close = frame["c"].astype(float)
    log_close = np.log(close)
    one_return = log_close.diff()
    frame["return_1"] = one_return
    frame["return_3"] = log_close.diff(3)
    frame["return_12"] = log_close.diff(12)
    frame["return_48"] = log_close.diff(48)
    frame["volatility_12"] = one_return.rolling(12).std()
    frame["volatility_48"] = one_return.rolling(48).std()
    frame["range_fraction"] = (frame["h"] - frame["l"]) / close
    frame["body_fraction"] = (frame["c"] - frame["o"]) / close
    log_volume = np.log1p(frame["v"].astype(float))
    frame["volume_z_48"] = (log_volume - log_volume.rolling(48).mean()) / log_volume.rolling(48).std()
    frame["vwap_distance"] = (close - frame["vw"].astype(float)) / close
    timestamp = pd.to_datetime(frame["t"], utc=True)
    hour = timestamp.dt.hour + timestamp.dt.minute / 60
    frame["hour_sin"] = np.sin(2 * np.pi * hour / 24)
    frame["hour_cos"] = np.cos(2 * np.pi * hour / 24)
    frame["target_return"] = log_close.shift(-horizon_bars) - log_close
    return frame.dropna(subset=FEATURE_NAMES).reset_index(drop=True)


def build_frame(bars: list[dict[str, Any]], horizon_bars: int = 3) -> pd.DataFrame:
    """Return only feature rows whose forward label is fully observed for evaluation."""
    return build_feature_frame(bars, horizon_bars).dropna(
        subset=["target_return"]
    ).reset_index(drop=True)


def chronological_slices(row_count: int, purge_rows: int = 3) -> tuple[slice, slice, slice]:
    if row_count < 1_000:
        raise ValueError("At least 1,000 complete observations are required.")
    train_end = int(row_count * 0.60)
    calibration_end = int(row_count * 0.80)
    return (
        slice(0, train_end - purge_rows),
        slice(train_end, calibration_end - purge_rows),
        slice(calibration_end, row_count),
    )


def rolling_outer_slices(row_count: int, horizon_bars: int) -> tuple[tuple[slice, slice, slice], ...]:
    """Return purged train, calibration, and independent test slices for rolling validation."""
    if row_count < 1_000:
        raise ValueError("At least 1,000 complete observations are required.")
    if horizon_bars < 1:
        raise ValueError("The prediction horizon must be positive.")
    windows: list[tuple[slice, slice, slice]] = []
    for train_fraction, calibration_fraction, test_fraction in ((0.4, 0.6, 0.8), (0.6, 0.8, 1.0)):
        train_end = int(row_count * train_fraction)
        calibration_end = int(row_count * calibration_fraction)
        test_end = int(row_count * test_fraction)
        windows.append(
            (
                slice(0, train_end - horizon_bars),
                slice(train_end, calibration_end - horizon_bars),
                slice(calibration_end, test_end - horizon_bars),
            )
        )
    return tuple(windows)


def conservative_lower_mean(values: NDArray[np.float64], one_sided_alpha: float = 0.025) -> float:
    """Return a lower confidence bound with an explicit comparison-adjusted alpha."""
    if len(values) < 2:
        return float("-inf")
    if not 0 < one_sided_alpha < 1:
        raise ValueError("one_sided_alpha must be between zero and one.")
    critical = NormalDist().inv_cdf(1 - one_sided_alpha)
    return float(values.mean() - critical * values.std(ddof=1) / math.sqrt(len(values)))


def non_overlapping_returns(
    predictions: NDArray[np.float64],
    realized: NDArray[np.float64],
    threshold: float,
    horizon_bars: int,
) -> NDArray[np.float64]:
    selected: list[float] = []
    next_eligible = 0
    for index, prediction in enumerate(predictions):
        if index >= next_eligible and prediction >= threshold:
            selected.append(float(realized[index]))
            next_eligible = index + horizon_bars
    return np.asarray(selected)


def select_threshold(
    predictions: NDArray[np.float64],
    realized: NDArray[np.float64],
    cost: float,
    horizon_bars: int,
) -> float:
    candidates = np.quantile(predictions, np.linspace(0.80, 0.995, 40))
    best_threshold = float("inf")
    best_lower = float("-inf")
    for threshold in candidates:
        selected = non_overlapping_returns(
            predictions, realized, float(threshold), horizon_bars
        ) - cost
        if len(selected) < 30:
            continue
        lower = conservative_lower_mean(selected)
        if lower > best_lower:
            best_lower = lower
            best_threshold = float(threshold)
    return best_threshold


def run_experiment(
    data_root: Path,
    round_trip_cost_bps: float = CRYPTO_TAKER_ROUND_TRIP_BPS_MEASURED,
    horizon_bars: int = 12,
    manifest_name: str = "latest-manifest.json",
    experiment_name: str = "crypto-direction",
) -> DirectionEvaluation:
    """Validate a causal directional model against one immutable dataset manifest."""
    manifest = json.loads((data_root / manifest_name).read_text(encoding="utf-8"))
    timeframe = str(manifest.get("timeframe", "unknown")).lower()
    bars = json.loads((data_root / require_manifest_value(manifest, "data_file")).read_text(encoding="utf-8"))
    frame = build_frame(bars, horizon_bars)
    train_slice, calibration_slice, test_slice = chronological_slices(len(frame), horizon_bars)
    features = frame.loc[:, FEATURE_NAMES].to_numpy(dtype=float)
    target = frame["target_return"].to_numpy(dtype=float)
    model = lgb.LGBMRegressor(
        objective="huber",
        n_estimators=500,
        learning_rate=0.025,
        num_leaves=15,
        max_depth=6,
        min_child_samples=100,
        subsample=0.8,
        colsample_bytree=0.8,
        reg_lambda=2.0,
        random_state=42,
        n_jobs=4,
        verbosity=-1,
    )
    model.fit(
        features[train_slice],
        target[train_slice],
        eval_X=features[calibration_slice],
        eval_y=target[calibration_slice],
        callbacks=[lgb.early_stopping(50, verbose=False)],
    )
    calibration_predictions = np.asarray(
        model.predict(features[calibration_slice]), dtype=np.float64
    )
    threshold = select_threshold(
        calibration_predictions,
        target[calibration_slice],
        round_trip_cost_bps / 10_000,
        horizon_bars,
    )
    test_predictions = np.asarray(model.predict(features[test_slice]), dtype=np.float64)
    selected = non_overlapping_returns(
        test_predictions, target[test_slice], threshold, horizon_bars
    ) - round_trip_cost_bps / 10_000
    mean = float(selected.mean()) if len(selected) else float("-inf")
    lower = conservative_lower_mean(selected)
    std = float(selected.std(ddof=1)) if len(selected) > 1 else 0.0
    sharpe = mean / std * math.sqrt(annual_periods(timeframe) / horizon_bars) if std > 0 else 0.0
    cumulative = np.cumsum(selected) if len(selected) else np.array([0.0])
    drawdown = cumulative - np.maximum.accumulate(cumulative)
    passed = len(selected) >= 30 and lower > 0 and sharpe > 0.5
    evaluation = DirectionEvaluation(
        passed=passed,
        score=round(lower * 10_000, 6) if math.isfinite(lower) else -1_000_000,
        calibration_threshold_bps=round(threshold * 10_000, 6) if math.isfinite(threshold) else 1_000_000,
        test_trade_count=len(selected),
        test_mean_net_bps=round(mean * 10_000, 6) if math.isfinite(mean) else -1_000_000,
        test_lower_confidence_net_bps=round(lower * 10_000, 6) if math.isfinite(lower) else -1_000_000,
        test_win_rate=round(float((selected > 0).mean()), 6) if len(selected) else 0.0,
        test_sharpe=round(sharpe, 6),
        test_max_drawdown_bps=round(float(drawdown.min()) * 10_000, 6),
        dataset_hash=manifest["sha256"],
    )
    TrialLedger().record_trial(
        {
            "experiment_id": f"{experiment_name}-lgbm-{horizon_bars}bar-v1",
            "hypothesis_family_id": f"{experiment_name}-{timeframe}-{horizon_bars}bar",
            "model_family": "lightgbm_huber",
            "feature_family": "causal_price_volume_v1",
            "parameters": {
                "round_trip_cost_bps": round_trip_cost_bps,
                "horizon_bars": horizon_bars,
                "features": FEATURE_NAMES,
            },
            "dataset_hash": manifest["sha256"],
            "sharpe_ratio": evaluation.test_sharpe,
            "status": "VALIDATION_PASS" if passed else "VALIDATION_FAIL",
            "git_commit": "working-tree",
            "config_hash": f"{experiment_name}-direction-v1",
        }
    )
    return evaluation


def run_rolling_experiment(
    data_root: Path,
    round_trip_cost_bps: float,
    horizon_bars: int,
    manifest_name: str,
    experiment_name: str,
) -> DirectionEvaluation:
    """Require two disjoint, chronological out-of-sample windows before promotion."""
    manifest = json.loads((data_root / manifest_name).read_text(encoding="utf-8"))
    timeframe = str(manifest.get("timeframe", "unknown")).lower()
    bars = json.loads((data_root / require_manifest_value(manifest, "data_file")).read_text(encoding="utf-8"))
    frame = build_frame(bars, horizon_bars)
    if len(frame) < 1_000:
        raise ValueError("At least 1,000 complete observations are required.")
    features = frame.loc[:, FEATURE_NAMES].to_numpy(dtype=float)
    target = frame["target_return"].to_numpy(dtype=float)
    costs = round_trip_cost_bps / 10_000
    selected_windows: list[np.ndarray] = []
    thresholds: list[float] = []
    for train_slice, calibration_slice, test_slice in rolling_outer_slices(
        len(frame), horizon_bars
    ):
        model = lgb.LGBMRegressor(
            objective="huber", n_estimators=500, learning_rate=0.025, num_leaves=15,
            max_depth=6, min_child_samples=100, subsample=0.8, colsample_bytree=0.8,
            reg_lambda=2.0, random_state=42, n_jobs=4, verbosity=-1,
        )
        model.fit(
            features[train_slice], target[train_slice],
            eval_X=features[calibration_slice],
            eval_y=target[calibration_slice],
            callbacks=[lgb.early_stopping(50, verbose=False)],
        )
        calibration_predictions = np.asarray(
            model.predict(features[calibration_slice]), dtype=np.float64
        )
        threshold = select_threshold(
            calibration_predictions, target[calibration_slice], costs, horizon_bars
        )
        # Keep outer test holding periods disjoint.  A row at the end of an
        # earlier test interval has a target that looks ahead by `horizon_bars`;
        # including it would reuse realized price moves from the next interval.
        # Dropping that tail also makes the final interval conservative.
        predictions = np.asarray(model.predict(features[test_slice]), dtype=np.float64)
        selected_windows.append(
            non_overlapping_returns(
                predictions,
                target[test_slice],
                threshold,
                horizon_bars,
            )
            - costs
        )
        thresholds.append(threshold)
    selected = np.concatenate(selected_windows)
    mean = float(selected.mean()) if len(selected) else float("-inf")
    lower = conservative_lower_mean(selected)
    std = float(selected.std(ddof=1)) if len(selected) > 1 else 0.0
    sharpe = mean / std * math.sqrt(annual_periods(timeframe) / horizon_bars) if std > 0 else 0.0
    cumulative = np.cumsum(selected) if len(selected) else np.array([0.0])
    drawdown = cumulative - np.maximum.accumulate(cumulative)
    passed = len(selected) >= 60 and lower > 0 and sharpe > 0.5
    evaluation = DirectionEvaluation(
        passed=passed,
        score=round(lower * 10_000, 6) if math.isfinite(lower) else -1_000_000,
        calibration_threshold_bps=round(float(np.mean(thresholds)) * 10_000, 6),
        test_trade_count=len(selected),
        test_mean_net_bps=round(mean * 10_000, 6) if math.isfinite(mean) else -1_000_000,
        test_lower_confidence_net_bps=round(lower * 10_000, 6) if math.isfinite(lower) else -1_000_000,
        test_win_rate=round(float((selected > 0).mean()), 6) if len(selected) else 0.0,
        test_sharpe=round(sharpe, 6),
        test_max_drawdown_bps=round(float(drawdown.min()) * 10_000, 6),
        dataset_hash=manifest["sha256"],
    )
    TrialLedger().record_trial(
        {
            "experiment_id": f"{experiment_name}-rolling-lgbm-{horizon_bars}bar-v1",
            "hypothesis_family_id": f"{experiment_name}-rolling-{timeframe}-{horizon_bars}bar",
            "model_family": "lightgbm_huber",
            "feature_family": "causal_price_volume_v1",
            "parameters": {
                "round_trip_cost_bps": round_trip_cost_bps,
                "horizon_bars": horizon_bars,
                "outer_test_windows": 2,
                "features": FEATURE_NAMES,
            },
            "dataset_hash": manifest["sha256"],
            "sharpe_ratio": evaluation.test_sharpe,
            "status": "VALIDATION_PASS" if evaluation.passed else "VALIDATION_FAIL",
            "git_commit": "working-tree",
            "config_hash": f"{experiment_name}-rolling-direction-v1",
        }
    )
    return evaluation


def run_rolling_persistence_baseline(
    data_root: Path,
    round_trip_cost_bps: float,
    horizon_bars: int,
    manifest_name: str,
    experiment_name: str,
) -> DirectionEvaluation:
    """Evaluate a causal prior-return baseline on the identical purged rolling windows."""
    manifest = json.loads((data_root / manifest_name).read_text(encoding="utf-8"))
    timeframe = str(manifest.get("timeframe", "unknown")).lower()
    bars = json.loads((data_root / require_manifest_value(manifest, "data_file")).read_text(encoding="utf-8"))
    frame = build_frame(bars, horizon_bars)
    target = frame["target_return"].to_numpy(dtype=float)
    predictions = frame["return_1"].to_numpy(dtype=float)
    costs = round_trip_cost_bps / 10_000
    selected_windows: list[np.ndarray] = []
    thresholds: list[float] = []
    for _, calibration_slice, test_slice in rolling_outer_slices(len(frame), horizon_bars):
        threshold = select_threshold(
            predictions[calibration_slice], target[calibration_slice], costs, horizon_bars
        )
        selected_windows.append(
            non_overlapping_returns(
                predictions[test_slice], target[test_slice], threshold, horizon_bars
            ) - costs
        )
        thresholds.append(threshold)
    selected = np.concatenate(selected_windows)
    mean = float(selected.mean()) if len(selected) else float("-inf")
    lower = conservative_lower_mean(selected)
    std = float(selected.std(ddof=1)) if len(selected) > 1 else 0.0
    sharpe = mean / std * math.sqrt(annual_periods(timeframe) / horizon_bars) if std > 0 else 0.0
    cumulative = np.cumsum(selected) if len(selected) else np.array([0.0])
    drawdown = cumulative - np.maximum.accumulate(cumulative)
    passed = len(selected) >= 60 and lower > 0 and sharpe > 0.5
    evaluation = DirectionEvaluation(
        passed=passed,
        score=round(lower * 10_000, 6) if math.isfinite(lower) else -1_000_000,
        calibration_threshold_bps=round(float(np.mean(thresholds)) * 10_000, 6),
        test_trade_count=len(selected),
        test_mean_net_bps=round(mean * 10_000, 6) if math.isfinite(mean) else -1_000_000,
        test_lower_confidence_net_bps=round(lower * 10_000, 6) if math.isfinite(lower) else -1_000_000,
        test_win_rate=round(float((selected > 0).mean()), 6) if len(selected) else 0.0,
        test_sharpe=round(sharpe, 6),
        test_max_drawdown_bps=round(float(drawdown.min()) * 10_000, 6),
        dataset_hash=manifest["sha256"],
    )
    TrialLedger().record_trial(
        {
            "experiment_id": f"{experiment_name}-rolling-persistence-{horizon_bars}bar-v1",
            "hypothesis_family_id": f"{experiment_name}-rolling-{timeframe}-{horizon_bars}bar",
            "model_family": "prior_return_persistence",
            "feature_family": "return_1_only",
            "parameters": {"round_trip_cost_bps": round_trip_cost_bps, "horizon_bars": horizon_bars},
            "dataset_hash": manifest["sha256"], "sharpe_ratio": evaluation.test_sharpe,
            "status": "VALIDATION_PASS" if passed else "VALIDATION_FAIL",
            "git_commit": "working-tree", "config_hash": f"{experiment_name}-persistence-v1",
        }
    )
    return evaluation


def run_rolling_low_vol_persistence_experiment(
    data_root: Path,
    round_trip_cost_bps: float,
    horizon_bars: int,
    manifest_name: str,
    experiment_name: str,
) -> DirectionEvaluation:
    """Test whether momentum survives costs only in a volatility regime chosen on calibration data."""
    manifest = json.loads((data_root / manifest_name).read_text(encoding="utf-8"))
    timeframe = str(manifest.get("timeframe", "unknown")).lower()
    bars = json.loads((data_root / require_manifest_value(manifest, "data_file")).read_text(encoding="utf-8"))
    frame = build_frame(bars, horizon_bars)
    target = frame["target_return"].to_numpy(dtype=float)
    predictions = frame["return_1"].to_numpy(dtype=float)
    volatility = frame["volatility_48"].to_numpy(dtype=float)
    costs = round_trip_cost_bps / 10_000
    # Four caps are the complete preregistered regime-search budget. Charge them during
    # calibration selection and untouched reporting, rather than treating the selected cap free.
    regime_comparison_count = 4
    regime_alpha = 0.025 / regime_comparison_count
    selected_windows: list[np.ndarray] = []
    thresholds: list[float] = []
    for _, calibration_slice, test_slice in rolling_outer_slices(len(frame), horizon_bars):
        calibration_predictions = predictions[calibration_slice]
        calibration_target = target[calibration_slice]
        calibration_volatility = volatility[calibration_slice]
        best_threshold = float("inf")
        best_cap = float("-inf")
        best_lower = float("-inf")
        for cap in np.quantile(calibration_volatility, (0.25, 0.5, 0.75, 1.0)):
            eligible = calibration_volatility <= cap
            threshold = select_threshold(
                calibration_predictions[eligible], calibration_target[eligible], costs, horizon_bars
            )
            returns = non_overlapping_returns(
                calibration_predictions[eligible], calibration_target[eligible], threshold, horizon_bars
            ) - costs
            lower = conservative_lower_mean(returns, regime_alpha)
            if lower > best_lower:
                best_threshold, best_cap, best_lower = threshold, float(cap), lower
        test_eligible = volatility[test_slice] <= best_cap
        selected_windows.append(
            non_overlapping_returns(
                predictions[test_slice][test_eligible], target[test_slice][test_eligible],
                best_threshold, horizon_bars,
            ) - costs
        )
        thresholds.append(best_threshold)
    selected = np.concatenate(selected_windows)
    mean = float(selected.mean()) if len(selected) else float("-inf")
    lower = conservative_lower_mean(selected, regime_alpha)
    std = float(selected.std(ddof=1)) if len(selected) > 1 else 0.0
    sharpe = mean / std * math.sqrt(annual_periods(timeframe) / horizon_bars) if std > 0 else 0.0
    cumulative = np.cumsum(selected) if len(selected) else np.array([0.0])
    drawdown = cumulative - np.maximum.accumulate(cumulative)
    passed = len(selected) >= 60 and lower > 0 and sharpe > 0.5
    evaluation = DirectionEvaluation(
        passed=passed,
        score=round(lower * 10_000, 6) if math.isfinite(lower) else -1_000_000,
        calibration_threshold_bps=round(float(np.mean(thresholds)) * 10_000, 6),
        test_trade_count=len(selected),
        test_mean_net_bps=round(mean * 10_000, 6) if math.isfinite(mean) else -1_000_000,
        test_lower_confidence_net_bps=round(lower * 10_000, 6) if math.isfinite(lower) else -1_000_000,
        test_win_rate=round(float((selected > 0).mean()), 6) if len(selected) else 0.0,
        test_sharpe=round(sharpe, 6),
        test_max_drawdown_bps=round(float(drawdown.min()) * 10_000, 6),
        dataset_hash=manifest["sha256"],
    )
    TrialLedger().record_trial(
        {
            "experiment_id": f"{experiment_name}-rolling-low-vol-persistence-{horizon_bars}bar-v1",
            "hypothesis_family_id": f"{experiment_name}-low-vol-{timeframe}-{horizon_bars}bar",
            "model_family": "low_volatility_conditioned_persistence",
            "feature_family": "return_1_with_calibrated_volatility_regime",
            "parameters": {
                "round_trip_cost_bps": round_trip_cost_bps,
                "horizon_bars": horizon_bars,
                "volatility_caps": [0.25, 0.5, 0.75, 1.0],
                "regime_comparison_count": regime_comparison_count,
                "one_sided_alpha": regime_alpha,
                "outer_test_windows": 2,
            },
            "dataset_hash": manifest["sha256"], "sharpe_ratio": evaluation.test_sharpe,
            "status": "VALIDATION_PASS" if passed else "VALIDATION_FAIL",
            "git_commit": "working-tree", "config_hash": f"{experiment_name}-low-vol-persistence-v1",
        }
    )
    return evaluation


def run_rolling_contrarian_baseline(
    data_root: Path,
    round_trip_cost_bps: float,
    horizon_bars: int,
    manifest_name: str,
    experiment_name: str,
) -> DirectionEvaluation:
    """Evaluate a causal one-bar mean-reversion signal on identical rolling windows."""
    manifest = json.loads((data_root / manifest_name).read_text(encoding="utf-8"))
    timeframe = str(manifest.get("timeframe", "unknown")).lower()
    bars = json.loads((data_root / require_manifest_value(manifest, "data_file")).read_text(encoding="utf-8"))
    frame = build_frame(bars, horizon_bars)
    target = frame["target_return"].to_numpy(dtype=float)
    predictions = -frame["return_1"].to_numpy(dtype=float)
    costs = round_trip_cost_bps / 10_000
    selected_windows: list[np.ndarray] = []
    thresholds: list[float] = []
    for _, calibration_slice, test_slice in rolling_outer_slices(len(frame), horizon_bars):
        threshold = select_threshold(
            predictions[calibration_slice], target[calibration_slice], costs, horizon_bars
        )
        selected_windows.append(
            non_overlapping_returns(
                predictions[test_slice], target[test_slice], threshold, horizon_bars
            ) - costs
        )
        thresholds.append(threshold)
    selected = np.concatenate(selected_windows)
    mean = float(selected.mean()) if len(selected) else float("-inf")
    lower = conservative_lower_mean(selected)
    std = float(selected.std(ddof=1)) if len(selected) > 1 else 0.0
    sharpe = mean / std * math.sqrt(annual_periods(timeframe) / horizon_bars) if std > 0 else 0.0
    cumulative = np.cumsum(selected) if len(selected) else np.array([0.0])
    drawdown = cumulative - np.maximum.accumulate(cumulative)
    passed = len(selected) >= 60 and lower > 0 and sharpe > 0.5
    evaluation = DirectionEvaluation(
        passed=passed,
        score=round(lower * 10_000, 6) if math.isfinite(lower) else -1_000_000,
        calibration_threshold_bps=round(float(np.mean(thresholds)) * 10_000, 6),
        test_trade_count=len(selected),
        test_mean_net_bps=round(mean * 10_000, 6) if math.isfinite(mean) else -1_000_000,
        test_lower_confidence_net_bps=round(lower * 10_000, 6) if math.isfinite(lower) else -1_000_000,
        test_win_rate=round(float((selected > 0).mean()), 6) if len(selected) else 0.0,
        test_sharpe=round(sharpe, 6),
        test_max_drawdown_bps=round(float(drawdown.min()) * 10_000, 6),
        dataset_hash=manifest["sha256"],
    )
    TrialLedger().record_trial(
        {
            "experiment_id": f"{experiment_name}-rolling-contrarian-{horizon_bars}bar-v1",
            "hypothesis_family_id": f"{experiment_name}-contrarian-{timeframe}-{horizon_bars}bar",
            "model_family": "one_bar_contrarian_baseline",
            "feature_family": "negative_return_1_only",
            "parameters": {"round_trip_cost_bps": round_trip_cost_bps, "horizon_bars": horizon_bars,
                           "outer_test_windows": 2},
            "dataset_hash": manifest["sha256"], "sharpe_ratio": evaluation.test_sharpe,
            "status": "VALIDATION_PASS" if passed else "VALIDATION_FAIL",
            "git_commit": "working-tree", "config_hash": f"{experiment_name}-contrarian-v1",
        }
    )
    return evaluation


def run_rolling_cross_asset_lead_experiment(
    data_root: Path,
    round_trip_cost_bps: float,
    horizon_bars: int,
    btc_manifest_name: str,
    eth_manifest_name: str,
    experiment_name: str,
) -> DirectionEvaluation:
    """Test whether ETH's completed return leads the next BTC return without look-ahead."""
    btc_manifest = json.loads((data_root / btc_manifest_name).read_text(encoding="utf-8"))
    eth_manifest = json.loads((data_root / eth_manifest_name).read_text(encoding="utf-8"))
    btc_bars = json.loads((data_root / require_manifest_value(btc_manifest, "data_file")).read_text(encoding="utf-8"))
    eth_bars = json.loads((data_root / require_manifest_value(eth_manifest, "data_file")).read_text(encoding="utf-8"))
    btc = build_frame(btc_bars, horizon_bars)[["t", "target_return"]]
    eth = build_feature_frame(eth_bars, horizon_bars)[["t", "return_1"]]
    frame = btc.merge(eth, on="t", how="inner").dropna().reset_index(drop=True)
    target = frame["target_return"].to_numpy(dtype=float)
    predictions = frame["return_1"].to_numpy(dtype=float)
    costs = round_trip_cost_bps / 10_000
    selected_windows: list[np.ndarray] = []
    thresholds: list[float] = []
    for _, calibration_slice, test_slice in rolling_outer_slices(len(frame), horizon_bars):
        threshold = select_threshold(
            predictions[calibration_slice], target[calibration_slice], costs, horizon_bars
        )
        selected_windows.append(
            non_overlapping_returns(
                predictions[test_slice], target[test_slice], threshold, horizon_bars
            ) - costs
        )
        thresholds.append(threshold)
    selected = np.concatenate(selected_windows)
    mean = float(selected.mean()) if len(selected) else float("-inf")
    lower = conservative_lower_mean(selected)
    std = float(selected.std(ddof=1)) if len(selected) > 1 else 0.0
    sharpe = mean / std * math.sqrt(annual_periods(str(btc_manifest["timeframe"])) / horizon_bars) if std > 0 else 0.0
    cumulative = np.cumsum(selected) if len(selected) else np.array([0.0])
    drawdown = cumulative - np.maximum.accumulate(cumulative)
    passed = len(selected) >= 60 and lower > 0 and sharpe > 0.5
    evaluation = DirectionEvaluation(
        passed=passed, score=round(lower * 10_000, 6) if math.isfinite(lower) else -1_000_000,
        calibration_threshold_bps=round(float(np.mean(thresholds)) * 10_000, 6),
        test_trade_count=len(selected), test_mean_net_bps=round(mean * 10_000, 6) if math.isfinite(mean) else -1_000_000,
        test_lower_confidence_net_bps=round(lower * 10_000, 6) if math.isfinite(lower) else -1_000_000,
        test_win_rate=round(float((selected > 0).mean()), 6) if len(selected) else 0.0,
        test_sharpe=round(sharpe, 6), test_max_drawdown_bps=round(float(drawdown.min()) * 10_000, 6),
        dataset_hash=btc_manifest["sha256"],
    )
    TrialLedger().record_trial({
        "experiment_id": f"{experiment_name}-rolling-eth-lead-{horizon_bars}bar-v1",
        "hypothesis_family_id": f"{experiment_name}-eth-lead-{horizon_bars}bar",
        "model_family": "cross_asset_eth_return_lead", "feature_family": "eth_return_1_only",
        "parameters": {"round_trip_cost_bps": round_trip_cost_bps, "horizon_bars": horizon_bars,
                       "outer_test_windows": 2, "source": "ETH/USD"},
        "dataset_hash": btc_manifest["sha256"], "sharpe_ratio": evaluation.test_sharpe,
        "status": "VALIDATION_PASS" if passed else "VALIDATION_FAIL", "git_commit": "working-tree",
        "config_hash": f"{experiment_name}-eth-lead-v1",
    })
    return evaluation


def run_rolling_relative_strength_experiment(
    data_root: Path,
    round_trip_cost_bps: float,
    horizon_bars: int,
    btc_manifest_name: str,
    eth_manifest_name: str,
    experiment_name: str,
) -> DirectionEvaluation:
    """Test whether completed ETH-versus-BTC relative strength predicts the next BTC return."""
    btc_manifest = json.loads((data_root / btc_manifest_name).read_text(encoding="utf-8"))
    eth_manifest = json.loads((data_root / eth_manifest_name).read_text(encoding="utf-8"))
    btc_bars = json.loads((data_root / require_manifest_value(btc_manifest, "data_file")).read_text(encoding="utf-8"))
    eth_bars = json.loads((data_root / require_manifest_value(eth_manifest, "data_file")).read_text(encoding="utf-8"))
    btc = build_frame(btc_bars, horizon_bars)[["t", "target_return", "return_1"]]
    eth = build_feature_frame(eth_bars, horizon_bars)[["t", "return_1"]].rename(
        columns={"return_1": "eth_return_1"}
    )
    frame = btc.merge(eth, on="t", how="inner").dropna().reset_index(drop=True)
    target = frame["target_return"].to_numpy(dtype=float)
    predictions = (frame["eth_return_1"] - frame["return_1"]).to_numpy(dtype=float)
    costs = round_trip_cost_bps / 10_000
    selected_windows: list[np.ndarray] = []
    thresholds: list[float] = []
    for _, calibration_slice, test_slice in rolling_outer_slices(len(frame), horizon_bars):
        threshold = select_threshold(predictions[calibration_slice], target[calibration_slice], costs, horizon_bars)
        selected_windows.append(non_overlapping_returns(
            predictions[test_slice], target[test_slice], threshold, horizon_bars
        ) - costs)
        thresholds.append(threshold)
    selected = np.concatenate(selected_windows)
    mean = float(selected.mean()) if len(selected) else float("-inf")
    lower = conservative_lower_mean(selected)
    std = float(selected.std(ddof=1)) if len(selected) > 1 else 0.0
    sharpe = mean / std * math.sqrt(annual_periods(str(btc_manifest["timeframe"])) / horizon_bars) if std > 0 else 0.0
    cumulative = np.cumsum(selected) if len(selected) else np.array([0.0])
    drawdown = cumulative - np.maximum.accumulate(cumulative)
    passed = len(selected) >= 60 and lower > 0 and sharpe > 0.5
    evaluation = DirectionEvaluation(
        passed=passed, score=round(lower * 10_000, 6) if math.isfinite(lower) else -1_000_000,
        calibration_threshold_bps=round(float(np.mean(thresholds)) * 10_000, 6),
        test_trade_count=len(selected), test_mean_net_bps=round(mean * 10_000, 6) if math.isfinite(mean) else -1_000_000,
        test_lower_confidence_net_bps=round(lower * 10_000, 6) if math.isfinite(lower) else -1_000_000,
        test_win_rate=round(float((selected > 0).mean()), 6) if len(selected) else 0.0,
        test_sharpe=round(sharpe, 6), test_max_drawdown_bps=round(float(drawdown.min()) * 10_000, 6),
        dataset_hash=btc_manifest["sha256"],
    )
    TrialLedger().record_trial({
        "experiment_id": f"{experiment_name}-rolling-relative-strength-{horizon_bars}bar-v1",
        "hypothesis_family_id": f"{experiment_name}-relative-strength-{horizon_bars}bar",
        "model_family": "eth_btc_relative_strength", "feature_family": "eth_return_1_minus_btc_return_1",
        "parameters": {"round_trip_cost_bps": round_trip_cost_bps, "horizon_bars": horizon_bars,
                       "outer_test_windows": 2}, "dataset_hash": btc_manifest["sha256"],
        "sharpe_ratio": evaluation.test_sharpe, "status": "VALIDATION_PASS" if passed else "VALIDATION_FAIL",
        "git_commit": "working-tree", "config_hash": f"{experiment_name}-relative-strength-v1",
    })
    return evaluation


def publish_validated_directional_forecast(
    data_root: Path,
    artifacts_root: Path,
    manifest_name: str,
    experiment_name: str,
    horizon_bars: int,
    evaluation: DirectionEvaluation,
    evidence_profile: EvidenceProfile,
    strategy_family: str,
) -> None:
    """Fit and publish only a rolling-validation-passed directional model contract bundle."""
    if not evaluation.passed:
        raise ValueError("A failed evaluation cannot be promoted.")
    manifest = json.loads((data_root / manifest_name).read_text(encoding="utf-8"))
    bars = json.loads((data_root / require_manifest_value(manifest, "data_file")).read_text(encoding="utf-8"))
    training_frame = build_frame(bars, horizon_bars)
    features = training_frame.loc[:, FEATURE_NAMES].to_numpy(dtype=float)
    target = training_frame["target_return"].to_numpy(dtype=float)
    model = lgb.LGBMRegressor(
        objective="huber", n_estimators=500, learning_rate=0.025, num_leaves=15,
        max_depth=6, min_child_samples=100, subsample=0.8, colsample_bytree=0.8,
        reg_lambda=2.0, random_state=42, n_jobs=4, verbosity=-1,
    )
    model.fit(features, target)
    artifact_id = f"{experiment_name}-{uuid4().hex}"
    model_payload = {"lightgbm_model": model.booster_.model_to_string()}
    encoded_model = json.dumps(model_payload, sort_keys=True).encode("utf-8")
    artifact_hash = hashlib.sha256(encoded_model).hexdigest()
    model_file = artifacts_root / f"{artifact_id}-model.json"
    artifacts_root.mkdir(parents=True, exist_ok=True)
    model_file.write_bytes(encoded_model)
    schema_version = "directional-price-volume-v1"
    feature_names = list(FEATURE_NAMES)
    dtypes = {name: "float64" for name in FEATURE_NAMES}
    source_requirements = ["alpaca_ohlcv"]
    schema_document = {
        "schema_version": schema_version,
        "feature_names": feature_names,
        "dtypes": dtypes,
        "normalization": {},
        "lookback_periods": 48,
        "source_requirements": source_requirements,
    }
    feature_hash = hashlib.sha256(
        json.dumps(schema_document, sort_keys=True).encode("utf-8")
    ).hexdigest()
    schema = FeatureSchema(
        schema_version=schema_version,
        feature_names=feature_names,
        dtypes=dtypes,
        normalization={},
        lookback_periods=48,
        source_requirements=source_requirements,
        feature_hash=feature_hash,
    )
    timeframe = str(manifest["timeframe"])
    horizon_minutes = horizon_bars * (5 if timeframe == "5Min" else 24 * 60)
    artifact = ModelArtifact(
        artifact_id=artifact_id, model_id=f"{experiment_name}-lgbm", model_type="lightgbm_huber",
        strategy_family=strategy_family,
        strategy_definition=StrategyDefinition(
            symbol=str(manifest["symbol"]),
            bar_duration_minutes=5 if timeframe == "5Min" else 24 * 60,
            forecast_horizon_minutes=horizon_minutes,
            entry_rule_version="directional-forecast-positive-v1",
            signal_type="State",
            parameters={"minimum_expected_return_bps": 0.0},
            exit_policy=ExitPolicyDefinition(
                policy_version="crypto-directional-managed-v1",
                maximum_holding_minutes=horizon_minutes,
                exit_on_thesis_invalidation=True,
                exit_on_regime_change=True,
            ),
        ),
        model_version="rolling-v1", feature_schema_hash=feature_hash, dataset_hash=manifest["sha256"],
        training_window={"end": manifest["end"]}, parameters={"horizon_bars": horizon_bars},
        random_seed=42, metrics=asdict(evaluation), evidence_grade=evidence_profile.transfer_grade,
        evidence_profile=evidence_profile, validation_gates=["R0", "R1", "R2", "R4"],
        validation_evidence={},
        support_domain={"instrument": manifest["symbol"], "timeframe": timeframe},
        git_commit="working-tree", config_hash=f"{experiment_name}-rolling-v1",
        creation_timestamp=datetime.now(UTC), artifact_hash=artifact_hash,
    )
    actionable_frame = build_feature_frame(bars, horizon_bars)
    actionable_features = actionable_frame.loc[:, FEATURE_NAMES].to_numpy(dtype=float)
    forecast_time = pd.Timestamp(actionable_frame.iloc[-1]["t"])
    if forecast_time.tzinfo is None:
        forecast_time = forecast_time.tz_localize(UTC)
    point_forecast = float(model.predict(actionable_features[-1:])[0] * 10_000)
    forecast = Forecast(
        expert_id=artifact.model_id, model_id=artifact.model_id, model_version=artifact.model_version,
        instrument=manifest["symbol"], as_of_time=forecast_time.to_pydatetime(),
        forecast_family="directional_return_bps", horizon_minutes=horizon_minutes,
        point_forecast=point_forecast, confidence=0.75, calibration_status="rolling_oos_pass",
        uncertainty=_forecast_uncertainty(evaluation),
        support_domain_status="in_domain", feature_schema_hash=feature_hash,
        artifact_hash=artifact_hash, status="valid",
    )
    registry = ModelRegistry(str(data_root / "experiments.db"))
    ContractPublisher(artifacts_root, registry).publish_validated(schema, artifact, forecast, model_file)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--data-root", type=Path, default=Path("data"))
    parser.add_argument(
        "--round-trip-cost-bps",
        type=float,
        default=None,
        help=(
            "Override the measured round-trip cost. Without this the cost is read from the "
            "published realised-cost dataset; there is deliberately no default, because a "
            "plausible-looking constant is how an assumed 60 bps outlived a measured 68."
        ),
    )
    parser.add_argument("--horizon-bars", type=int, default=12)
    parser.add_argument("--manifest-name", default="latest-manifest.json")
    parser.add_argument("--experiment-name", default="crypto-direction")
    arguments = parser.parse_args()

    # Resolved here rather than defaulted in the parser, so the run reports which of the two it got.
    cost_bps, provenance = resolve_round_trip_bps(
        arguments.data_root, arguments.round_trip_cost_bps
    )
    print(f"round-trip cost: {provenance}", file=sys.stderr)

    result = run_experiment(
        arguments.data_root,
        cost_bps,
        arguments.horizon_bars,
        arguments.manifest_name,
        arguments.experiment_name,
    )
    print(json.dumps(asdict(result), sort_keys=True))
    return 0 if result.passed else 1


if __name__ == "__main__":
    raise SystemExit(main())


def _forecast_uncertainty(evaluation: DirectionEvaluation) -> ForecastUncertainty:
    """State how wrong this forecast could be, and what the model actually earned.

    Two things are being said, and they are not the same thing. ``point_forecast`` above is the
    model's raw per-bar prediction -- gross, owing nothing to any cost assumption, which is why
    ``assumed_round_trip_cost_bps`` is zero here and non-zero for the deterministic-rule publisher
    whose forecast is already net. ``historical_net_edge_bps`` is what the model's trades actually
    returned after costs across the validation window, which is the only evidence that the
    predictions are worth acting on at all.

    The standard error is the dispersion of those realised trades, not of the model's residuals. It
    is the honest proxy: a per-bar prediction interval would describe how well the model fits, while
    what a trading decision needs to know is how much the *outcomes* of acting on it have varied.
    It is recovered from the same bound the promotion gates were applied against, so the published
    figure cannot disagree with the one that qualified the model.
    """
    critical = NormalDist().inv_cdf(1 - 0.025)
    standard_error = max(
        (evaluation.test_mean_net_bps - evaluation.test_lower_confidence_net_bps) / critical, 0.0
    )
    return ForecastUncertainty(
        standard_error_bps=standard_error,
        historical_net_edge_bps=evaluation.test_mean_net_bps,
        historical_net_edge_standard_error_bps=standard_error,
        historical_observations=evaluation.test_trade_count,
        assumed_round_trip_cost_bps=0.0,
    )
