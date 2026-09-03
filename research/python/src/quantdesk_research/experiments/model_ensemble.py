"""Three models and their average, measured against each other on the same out-of-sample windows.

Why three, and why these three
------------------------------
Ridge is here to be beaten. It is the linear baseline, and the comparison only means something if
the baseline is run under identical conditions rather than quoted from memory -- across 46 countries
plain OLS beat eight machine-learning methods on risk-adjusted returns, because estimation error
dominates when the panel is small, and seven crypto pairs plus four ETFs is a small panel. LightGBM
and a random forest are the two nonlinear families that fit on twelve CPU cores; 4 GB of VRAM rules
out anything sequential, so the hardware picks the shortlist as much as the literature does.

The fourth entry is the simple average of the three. Forecast combination beating its own members is
one of the oldest reliable findings in forecasting, and it costs nothing to test alongside them.

What this module does *not* do
------------------------------
It does not publish. It measures, and it reports what it measured. The top of PLAN.md records why
that ordering matters: crypto costs 60 bps a round trip against a best measured signal near 20 bps
gross, so a model wired into the trading path before anyone knows whether it clears the toll is
decoration. Every model here is scored net of its venue's real round trip, and a model whose lower
confidence bound sits below zero has not earned a place in the lane -- which is a result, not a
failure of the experiment.

Discipline borrowed rather than reinvented
------------------------------------------
The windows, the purging, the threshold selection on calibration and the non-overlapping return
accounting all come from ``crypto_direction``. Writing a second, subtly different evaluation
alongside the first is how two numbers that should agree stop agreeing, and neither can be trusted
afterwards.
"""

from __future__ import annotations

import json
import math
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Protocol

import numpy as np
from numpy.typing import NDArray
from sklearn.ensemble import RandomForestRegressor
from sklearn.linear_model import Ridge
from sklearn.pipeline import Pipeline
from sklearn.preprocessing import StandardScaler

from quantdesk_research.experiments.crypto_direction import (
    FEATURE_NAMES,
    annual_periods,
    build_frame,
    conservative_lower_mean,
    non_overlapping_returns,
    rolling_outer_slices,
    select_threshold,
)

#: What the venue actually charges, per asset class, for one round trip in basis points.
#:
#: Not the research assumption. The measured crypto figure is 60 bps against a research constant of
#: 33.7, and every crypto net figure computed against the constant was about 26 bps too generous --
#: which is the whole reason crypto rules cleared a committee floor that honest equity rules did not.
ROUND_TRIP_COST_BPS: dict[str, float] = {"spot_crypto": 60.0, "us_equity": 8.0}

#: Bars ahead the label looks. Five-minute bars, so three is fifteen minutes.
DEFAULT_HORIZON_BARS = 3

#: Below this, a mean is an anecdote. The same floor the rolling direction experiment applies.
MINIMUM_TEST_TRADES = 60


class Regressor(Protocol):
    """The scikit-learn shape, which LightGBM also implements."""

    def fit(self, X: NDArray[np.float64], y: NDArray[np.float64]) -> Any: ...

    def predict(self, X: NDArray[np.float64]) -> NDArray[np.float64]: ...


@dataclass(frozen=True)
class ModelScore:
    """One model's out-of-sample record on one instrument, net of that venue's round trip."""

    model: str
    symbol: str
    trades: int
    mean_net_bps: float
    lower_confidence_net_bps: float
    win_rate: float
    sharpe: float
    threshold_bps: float

    @property
    def clears_costs(self) -> bool:
        """Whether the evidence supports trading it, rather than whether the mean happened to be positive.

        The lower bound, not the mean. A mean above zero with an error bar straddling it is a model
        that has not been shown to earn anything, and treating it as one is how a desk pays sixty
        basis points a round trip to discover noise.
        """
        return (
            self.trades >= MINIMUM_TEST_TRADES
            and math.isfinite(self.lower_confidence_net_bps)
            and self.lower_confidence_net_bps > 0.0
        )


def build_models(random_state: int = 42) -> dict[str, Regressor]:
    """The shortlist, constructed identically for every instrument.

    Ridge is scaled because it is penalised: its features here span log returns near 1e-4 and hour
    sines near 1, and an unscaled penalty would be applied to coefficients whose natural sizes differ
    by four orders of magnitude -- which regularises the small-scale features almost out of
    existence and leaves the comparison measuring the scaling rather than the model. The trees are
    scale-invariant and are left alone.
    """
    import lightgbm as lgb  # imported here so the module loads without LightGBM present

    return {
        "ridge": Pipeline(
            [("scale", StandardScaler()), ("model", Ridge(alpha=1.0, random_state=random_state))]
        ),
        "lightgbm": lgb.LGBMRegressor(
            objective="huber",
            n_estimators=500,
            learning_rate=0.025,
            num_leaves=15,
            max_depth=6,
            min_child_samples=100,
            subsample=0.8,
            colsample_bytree=0.8,
            reg_lambda=2.0,
            random_state=random_state,
            n_jobs=4,
            verbosity=-1,
        ),
        "random_forest": RandomForestRegressor(
            n_estimators=300,
            max_depth=8,
            min_samples_leaf=100,
            max_features="sqrt",
            random_state=random_state,
            n_jobs=4,
        ),
    }


def _score(
    selected: NDArray[np.float64],
    thresholds: list[float],
    model: str,
    symbol: str,
    timeframe: str,
    horizon_bars: int,
) -> ModelScore:
    if len(selected) == 0:
        return ModelScore(model, symbol, 0, float("-inf"), float("-inf"), 0.0, 0.0, 0.0)

    mean = float(selected.mean())
    lower = conservative_lower_mean(selected)
    std = float(selected.std(ddof=1)) if len(selected) > 1 else 0.0
    sharpe = (
        mean / std * math.sqrt(annual_periods(timeframe) / horizon_bars) if std > 0 else 0.0
    )
    return ModelScore(
        model=model,
        symbol=symbol,
        trades=len(selected),
        mean_net_bps=round(mean * 10_000, 4),
        lower_confidence_net_bps=(
            round(lower * 10_000, 4) if math.isfinite(lower) else float("-inf")
        ),
        win_rate=round(float((selected > 0).mean()), 4),
        sharpe=round(sharpe, 4),
        threshold_bps=round(float(np.mean(thresholds)) * 10_000, 4) if thresholds else 0.0,
    )


def evaluate_instrument(
    bars: list[dict[str, Any]],
    *,
    symbol: str,
    timeframe: str,
    round_trip_cost_bps: float,
    horizon_bars: int = DEFAULT_HORIZON_BARS,
    random_state: int = 42,
) -> list[ModelScore]:
    """Score every model and their average on the same purged rolling windows.

    Each model gets its own threshold, chosen on calibration and applied to test, because a
    threshold is part of the model rather than a property of the market: the average's predictions
    have a different scale from any member's, and reusing a member's cut would measure the scale
    rather than the signal.

    The average is combined before thresholding, not after. Averaging the *decisions* would be a
    vote, which is a different and weaker object than a combined forecast -- three models that all
    lean slightly the same way carry information a majority vote throws away.
    """
    frame = build_frame(bars, horizon_bars)
    if len(frame) < 1_000:
        raise ValueError(
            f"{symbol}: {len(frame)} complete observations; at least 1,000 are required."
        )

    features = frame.loc[:, FEATURE_NAMES].to_numpy(dtype=float)
    target = frame["target_return"].to_numpy(dtype=float)
    cost = round_trip_cost_bps / 10_000

    names = [*build_models(random_state), "average"]
    windows: dict[str, list[NDArray[np.float64]]] = {name: [] for name in names}
    thresholds: dict[str, list[float]] = {name: [] for name in names}

    for train_slice, calibration_slice, test_slice in rolling_outer_slices(
        len(frame), horizon_bars
    ):
        calibration_predictions: dict[str, NDArray[np.float64]] = {}
        test_predictions: dict[str, NDArray[np.float64]] = {}

        for name, model in build_models(random_state).items():
            model.fit(features[train_slice], target[train_slice])
            calibration_predictions[name] = np.asarray(
                model.predict(features[calibration_slice]), dtype=np.float64
            )
            test_predictions[name] = np.asarray(
                model.predict(features[test_slice]), dtype=np.float64
            )

        calibration_predictions["average"] = np.mean(
            list(calibration_predictions.values()), axis=0
        )
        test_predictions["average"] = np.mean(list(test_predictions.values()), axis=0)

        for name in names:
            threshold = select_threshold(
                calibration_predictions[name], target[calibration_slice], cost, horizon_bars
            )
            thresholds[name].append(threshold)
            windows[name].append(
                non_overlapping_returns(
                    test_predictions[name], target[test_slice], threshold, horizon_bars
                )
                - cost
            )

    return [
        _score(
            np.concatenate(windows[name]),
            [t for t in thresholds[name] if math.isfinite(t)],
            name,
            symbol,
            timeframe,
            horizon_bars,
        )
        for name in names
    ]


def _asset_class(symbol: str) -> str:
    """Alpaca's crypto pairs are slash-separated and its equities are not."""
    return "spot_crypto" if "/" in symbol else "us_equity"


def evaluate_all(
    data_root: Path,
    *,
    horizon_bars: int = DEFAULT_HORIZON_BARS,
    random_state: int = 42,
) -> list[ModelScore]:
    """Score every instrument with a five-minute dataset on the volume.

    Reuses the fitting loop's discovery rather than a second list of symbols, so an instrument
    cannot be measured here and missed there.
    """
    from quantdesk_research.runtime.model_fitting import _five_minute_manifests

    scores: list[ModelScore] = []
    for manifest in _five_minute_manifests(data_root):
        symbol = str(manifest["symbol"])
        data_file = data_root / str(manifest["dataFile"])
        if not data_file.exists():
            continue

        bars = json.loads(data_file.read_text(encoding="utf-8"))
        try:
            scores.extend(
                evaluate_instrument(
                    bars,
                    symbol=symbol,
                    timeframe=str(manifest["timeframe"]).lower(),
                    round_trip_cost_bps=ROUND_TRIP_COST_BPS[_asset_class(symbol)],
                    horizon_bars=horizon_bars,
                    random_state=random_state,
                )
            )
        except (ValueError, KeyError):
            # Too little history, or a dataset this shape cannot be read. Skipping one instrument
            # is better than losing the comparison for all of them.
            continue

    return scores


def report(scores: list[ModelScore]) -> str:
    """A table an operator can read, ordered so the honest answer is visible first."""
    if not scores:
        return "no instrument produced a scored model"

    header = (
        f"{'symbol':<10} {'model':<14} {'trades':>7} {'mean':>10} "
        f"{'lower':>10} {'win':>7} {'sharpe':>8}  clears"
    )
    lines = [header, "-" * len(header)]
    for score in sorted(scores, key=lambda s: (s.symbol, s.model)):
        lines.append(
            f"{score.symbol:<10} {score.model:<14} {score.trades:>7} "
            f"{score.mean_net_bps:>10.2f} {score.lower_confidence_net_bps:>10.2f} "
            f"{score.win_rate:>7.3f} {score.sharpe:>8.2f}  "
            f"{'yes' if score.clears_costs else 'no'}"
        )

    cleared = [score for score in scores if score.clears_costs]
    lines.append("")
    lines.append(
        f"{len(cleared)} of {len(scores)} model/instrument pairs clear their venue's round trip "
        "at the lower confidence bound."
    )
    return "\n".join(lines)
