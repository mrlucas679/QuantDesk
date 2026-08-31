import argparse
import json
import math
from dataclasses import asdict, dataclass
from pathlib import Path
from statistics import NormalDist
from typing import Any

import numpy as np
import pandas as pd  # type: ignore[import-untyped]
from numpy.typing import NDArray

from quantdesk_research.experiments.prospective_campaign import (
    IndependentValidationCampaign,
    ProspectiveCampaign,
)


@dataclass(frozen=True)
class StrategyEvaluation:
    name: str
    passed: bool
    score: float
    trade_count: int
    mean_net_bps: float
    lower_confidence_net_bps: float
    win_rate: float
    sharpe: float
    maximum_drawdown_bps: float


def build_strategy_frame(bars: list[dict[str, Any]], horizon_bars: int) -> pd.DataFrame:
    """Build causal strategy signals and forward returns from chronological bars."""
    frame = pd.DataFrame(bars).sort_values("t").drop_duplicates("t").reset_index(drop=True)
    close = frame["c"].astype(float)
    returns = np.log(close).diff()
    prior_high = close.shift(1).rolling(48).max()
    fast = close.rolling(12).mean()
    slow = close.rolling(48).mean()
    mean = close.rolling(48).mean()
    deviation = close.rolling(48).std()
    gains = returns.clip(lower=0).rolling(14).mean()
    losses = (-returns.clip(upper=0)).rolling(14).mean()
    rsi = 100 - 100 / (1 + gains / losses.replace(0, np.nan))
    short_volatility = returns.rolling(12).std()
    long_volatility = returns.rolling(48).std()
    log_volume = np.log1p(frame["v"].astype(float))
    volume_z_score = (log_volume - log_volume.rolling(48).mean()) / log_volume.rolling(48).std()
    positive_trend = fast > slow
    weekly_return = np.log(close / close.shift(2_016))
    four_week_return = np.log(close / close.shift(8_064))
    prior_four_week_high = close.shift(1).rolling(8_064).max()

    frame["donchian_breakout"] = close > prior_high
    frame["moving_average_trend"] = positive_trend & (fast.shift(1) <= slow.shift(1))
    frame["bollinger_reversion"] = close < mean - 2 * deviation
    frame["rsi_reversion"] = rsi < 25
    frame["volatility_breakout"] = (close > prior_high) & (short_volatility > 1.5 * long_volatility)
    frame["volume_confirmed_breakout"] = (close > prior_high) & (volume_z_score > 2)
    frame["compression_breakout"] = (close > prior_high) & (
        short_volatility.shift(1) < 0.75 * long_volatility.shift(1)
    )
    frame["regime_ensemble"] = np.where(
        short_volatility > long_volatility,
        frame["donchian_breakout"],
        frame["bollinger_reversion"],
    ).astype(bool)
    frame["weekly_time_series_momentum"] = weekly_return > 0
    frame["four_week_time_series_momentum"] = four_week_return > 0
    frame["dual_horizon_momentum"] = (weekly_return > 0) & (four_week_return > 0)
    frame["four_week_breakout"] = close > prior_four_week_high
    frame["target_return"] = np.log(close.shift(-horizon_bars) / close)
    return frame.dropna().reset_index(drop=True)


def rolling_slices(row_count: int, horizon_bars: int) -> tuple[slice, slice]:
    """Return two disjoint, purged outer test windows."""
    if row_count < 1_000:
        raise ValueError("At least 1,000 complete observations are required.")
    return (
        slice(int(row_count * 0.60), int(row_count * 0.80) - horizon_bars),
        slice(int(row_count * 0.80), row_count - horizon_bars),
    )


def non_overlapping(
    signal: NDArray[np.bool_], returns: NDArray[np.float64], horizon: int
) -> NDArray[np.float64]:
    """Select causal signaled returns without overlapping holding intervals."""
    selected: list[float] = []
    next_eligible = 0
    for index, active in enumerate(signal):
        if active and index >= next_eligible:
            selected.append(float(returns[index]))
            next_eligible = index + horizon
    return np.asarray(selected, dtype=np.float64)


def finite_bps(value: float) -> float:
    """Convert a finite return to basis points and preserve failed empty samples."""
    return round(value * 10_000, 6) if math.isfinite(value) else -1_000_000


def evaluate_strategy(
    name: str,
    frame: pd.DataFrame,
    horizon_bars: int,
    round_trip_cost_bps: float,
) -> StrategyEvaluation:
    """Evaluate one fixed strategy on both untouched chronological windows."""
    costs = round_trip_cost_bps / 10_000
    samples = []
    for test_slice in rolling_slices(len(frame), horizon_bars):
        signal = frame[name].iloc[test_slice].to_numpy(dtype=bool)
        realized = frame["target_return"].iloc[test_slice].to_numpy(dtype=np.float64)
        samples.append(non_overlapping(signal, realized, horizon_bars) - costs)
    selected = np.concatenate(samples)
    count = len(selected)
    mean = float(selected.mean()) if count else float("-inf")
    standard_deviation = float(selected.std(ddof=1)) if count > 1 else 0.0
    lower = mean - 1.96 * standard_deviation / math.sqrt(count) if count > 1 else float("-inf")
    sharpe = (
        mean / standard_deviation * math.sqrt(365 * 24 * 12 / horizon_bars)
        if standard_deviation
        else 0.0
    )
    cumulative = np.cumsum(selected) if count else np.asarray([0.0])
    drawdown = cumulative - np.maximum.accumulate(cumulative)
    passed = count >= 60 and lower > 0 and sharpe > 0.5
    return StrategyEvaluation(
        name=name,
        passed=passed,
        score=finite_bps(lower),
        trade_count=count,
        mean_net_bps=finite_bps(mean),
        lower_confidence_net_bps=finite_bps(lower),
        win_rate=round(float((selected > 0).mean()), 6) if count else 0.0,
        sharpe=round(sharpe, 6),
        maximum_drawdown_bps=round(float(drawdown.min()) * 10_000, 6),
    )


def evaluate_prospective_strategy(
    name: str,
    frame: pd.DataFrame,
    horizon_bars: int,
    campaign: ProspectiveCampaign | IndependentValidationCampaign,
    comparison_count: int,
) -> StrategyEvaluation:
    """Evaluate one preregistered candidate with a multiplicity-adjusted confidence bound."""
    signal = frame[name].to_numpy(dtype=bool)
    realized = frame["target_return"].to_numpy(dtype=np.float64)
    selected = (
        non_overlapping(signal, realized, horizon_bars) - campaign.round_trip_cost_bps / 10_000
    )
    count = len(selected)
    mean = float(selected.mean()) if count else float("-inf")
    standard_deviation = float(selected.std(ddof=1)) if count > 1 else 0.0
    critical = NormalDist().inv_cdf(1 - 0.05 / (2 * comparison_count))
    lower = mean - critical * standard_deviation / math.sqrt(count) if count > 1 else float("-inf")
    sharpe = (
        mean / standard_deviation * math.sqrt(365 * 24 * 12 / horizon_bars)
        if standard_deviation
        else 0.0
    )
    cumulative = np.cumsum(selected) if count else np.asarray([0.0])
    drawdown = cumulative - np.maximum.accumulate(cumulative)
    passed = (
        count >= campaign.minimum_trades
        and finite_bps(lower) > campaign.required_lower_confidence_bps
        and sharpe > campaign.minimum_sharpe
    )
    return StrategyEvaluation(
        name=f"{name}:{horizon_bars}",
        passed=passed,
        score=finite_bps(lower),
        trade_count=count,
        mean_net_bps=finite_bps(mean),
        lower_confidence_net_bps=finite_bps(lower),
        win_rate=round(float((selected > 0).mean()), 6) if count else 0.0,
        sharpe=round(sharpe, 6),
        maximum_drawdown_bps=round(float(drawdown.min()) * 10_000, 6),
    )


def run_campaign(
    data_root: Path,
    manifest_name: str = "latest-manifest.json",
    horizon_bars: int = 12,
    round_trip_cost_bps: float = 60.0,
) -> list[StrategyEvaluation]:
    """Run the preregistered fixed-family campaign without promoting a winner."""
    manifest = json.loads((data_root / manifest_name).read_text(encoding="utf-8"))
    bars = json.loads((data_root / manifest["dataFile"]).read_text(encoding="utf-8"))
    frame = build_strategy_frame(bars, horizon_bars)
    names = (
        "donchian_breakout",
        "moving_average_trend",
        "bollinger_reversion",
        "rsi_reversion",
        "volatility_breakout",
        "regime_ensemble",
        "volume_confirmed_breakout",
        "compression_breakout",
    )
    return [evaluate_strategy(name, frame, horizon_bars, round_trip_cost_bps) for name in names]


def run_prospective_campaign(
    data_root: Path,
    campaign_path: Path,
    manifest_name: str = "latest-manifest.json",
) -> list[StrategyEvaluation]:
    """Evaluate only genuinely unseen bars for the immutable preregistered cohort."""
    campaign = ProspectiveCampaign.load(campaign_path)
    manifest = json.loads((data_root / manifest_name).read_text(encoding="utf-8"))
    if manifest["symbol"] != campaign.instrument or manifest["timeframe"] != campaign.timeframe:
        raise ValueError("PROSPECTIVE_SUPPORT_DOMAIN_MISMATCH")
    bars = json.loads((data_root / manifest["dataFile"]).read_text(encoding="utf-8"))
    campaign.require_sufficient_unseen_data(bars)
    comparison_count = len(campaign.strategy_families) * len(campaign.holding_horizons_bars)
    results: list[StrategyEvaluation] = []
    for horizon in campaign.holding_horizons_bars:
        frame = build_strategy_frame(bars, horizon)
        timestamps = pd.to_datetime(frame["t"], utc=True)
        unseen = frame[timestamps > campaign.holdout_start_exclusive].reset_index(drop=True)
        for family in campaign.strategy_families:
            results.append(
                evaluate_prospective_strategy(family, unseen, horizon, campaign, comparison_count)
            )
    return results


def run_independent_validation_campaign(
    data_root: Path,
    campaign_path: Path,
    manifest_name: str = "independent-validation-manifest.json",
) -> list[StrategyEvaluation]:
    """Evaluate the fixed strategy cohort once on a disjoint historical validation interval."""
    campaign = IndependentValidationCampaign.load(campaign_path)
    manifest = json.loads((data_root / manifest_name).read_text(encoding="utf-8"))
    if manifest["symbol"] != campaign.instrument or manifest["timeframe"] != campaign.timeframe:
        raise ValueError("INDEPENDENT_SUPPORT_DOMAIN_MISMATCH")
    bars = json.loads((data_root / manifest["dataFile"]).read_text(encoding="utf-8"))
    timestamps = pd.to_datetime([bar["t"] for bar in bars], utc=True)
    in_cohort = [
        bar
        for bar, timestamp in zip(bars, timestamps, strict=True)
        if campaign.validation_start_inclusive <= timestamp < campaign.validation_end_exclusive
    ]
    if len(in_cohort) < campaign.minimum_validation_bars:
        raise ValueError(
            f"INDEPENDENT_VALIDATION_INSUFFICIENT:{len(in_cohort)}/"
            f"{campaign.minimum_validation_bars}"
        )
    comparison_count = (
        campaign.prior_comparisons
        + len(campaign.strategy_families) * len(campaign.holding_horizons_bars)
    )
    results: list[StrategyEvaluation] = []
    for horizon in campaign.holding_horizons_bars:
        frame = build_strategy_frame(in_cohort, horizon)
        for family in campaign.strategy_families:
            results.append(
                evaluate_prospective_strategy(family, frame, horizon, campaign, comparison_count)
            )
    return results


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--data-root", type=Path, default=Path("data"))
    parser.add_argument("--manifest-name", default="latest-manifest.json")
    parser.add_argument("--horizon-bars", type=int, default=12)
    parser.add_argument("--round-trip-cost-bps", type=float, default=60.0)
    arguments = parser.parse_args()
    results = run_campaign(
        arguments.data_root,
        arguments.manifest_name,
        arguments.horizon_bars,
        arguments.round_trip_cost_bps,
    )
    print(json.dumps([asdict(result) for result in results], sort_keys=True))
    return 0 if any(result.passed for result in results) else 1


if __name__ == "__main__":
    raise SystemExit(main())
