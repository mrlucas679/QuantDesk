"""The comparison harness itself, checked on data whose answer is known in advance.

A measurement module is only worth its output if it cannot flatter what it measures. Two things
here would do that silently: letting a model choose its threshold on the window it is scored on,
and combining the members' decisions rather than their forecasts. Both produce plausible tables.
"""

from __future__ import annotations

import json
from datetime import UTC, datetime, timedelta
from pathlib import Path
from typing import Any

import numpy as np
import pytest

from quantdesk_research.experiments.model_ensemble import (
    ROUND_TRIP_COST_BPS,
    ModelScore,
    build_models,
    evaluate_all,
    evaluate_instrument,
    report,
)


def _bars(count: int = 4_000, seed: int = 7, drift: float = 0.0) -> list[dict[str, Any]]:
    """A price series with no forecastable structure beyond the drift given to it."""
    rng = np.random.default_rng(seed)
    closes = 100.0 * np.exp(np.cumsum(rng.normal(drift, 0.0015, size=count)))
    start = datetime(2026, 1, 1, tzinfo=UTC)
    return [
        {
            "t": (start + timedelta(minutes=5 * index)).isoformat(),
            "o": float(close),
            "h": float(close) * 1.0005,
            "l": float(close) * 0.9995,
            "c": float(close),
            "v": 1_000.0 + index % 97,
            "vw": float(close),
        }
        for index, close in enumerate(closes)
    ]


def test_the_shortlist_is_three_named_families() -> None:
    """Three, not seven. Estimation error dominates on a panel this small."""
    assert set(build_models()) == {"ridge", "lightgbm", "random_forest"}


def test_every_model_and_their_average_is_scored() -> None:
    scores = evaluate_instrument(
        _bars(), symbol="SPY", timeframe="5min", round_trip_cost_bps=8.0
    )

    assert {score.model for score in scores} == {
        "ridge",
        "lightgbm",
        "random_forest",
        "average",
    }
    assert all(score.symbol == "SPY" for score in scores)


def test_noise_does_not_clear_its_costs() -> None:
    """The load-bearing assertion.

    A random walk has no forecastable direction, so a harness that reports one of these models
    clearing a round trip on it is measuring its own leakage. This is the test that fails if
    thresholds are ever chosen on the window they are scored on.
    """
    scores = evaluate_instrument(
        _bars(seed=11),
        symbol="SPY",
        timeframe="5min",
        round_trip_cost_bps=8.0,
    )

    assert not any(score.clears_costs for score in scores)


def test_a_positive_mean_with_a_negative_bound_does_not_count_as_edge() -> None:
    """Mean above zero, error bar straddling it. That is a model not yet shown to earn anything.

    Reading the mean alone is how a desk pays a real round trip to discover noise -- and it is the
    exact shape of the most promising equity results at longer horizons, so the distinction is not
    hypothetical here.
    """
    promising = ModelScore(
        model="average",
        symbol="IWM",
        trades=81,
        mean_net_bps=17.09,
        lower_confidence_net_bps=-7.49,
        win_rate=0.605,
        sharpe=1.2,
        threshold_bps=4.0,
    )

    assert not promising.clears_costs


def test_too_few_trades_is_not_evidence_however_good_the_bound() -> None:
    anecdote = ModelScore(
        model="ridge",
        symbol="SPY",
        trades=12,
        mean_net_bps=40.0,
        lower_confidence_net_bps=25.0,
        win_rate=0.9,
        sharpe=3.0,
        threshold_bps=1.0,
    )

    assert not anecdote.clears_costs


def test_the_venue_cost_is_the_measured_one_not_the_research_assumption() -> None:
    """Crypto charges 60 bps a round trip; the research constant said 33.7.

    Every crypto net figure computed against the constant was about 26 bps too generous, which is
    why crypto rules cleared a committee floor that honest equity rules did not.
    """
    assert ROUND_TRIP_COST_BPS["spot_crypto"] == 60.0
    assert ROUND_TRIP_COST_BPS["us_equity"] == 8.0


def test_a_short_series_is_refused_rather_than_scored_thinly() -> None:
    with pytest.raises(ValueError):
        evaluate_instrument(
            _bars(count=600), symbol="SPY", timeframe="5min", round_trip_cost_bps=8.0
        )


def test_evaluation_covers_whatever_the_fitting_loop_would_fit(tmp_path: Path) -> None:
    """Discovery is shared with the fitting loop, so nothing can be fitted but never measured."""
    (tmp_path / "spy-5min.json").write_text(json.dumps(_bars()), encoding="utf-8")
    (tmp_path / "latest-spy-5min-iex.manifest.json").write_text(
        json.dumps(
            {
                "symbol": "SPY",
                "timeframe": "5Min",
                "sha256": "sha256:abc",
                "dataFile": "spy-5min.json",
                "generatedAt": datetime.now(UTC).isoformat(),
            }
        ),
        encoding="utf-8",
    )

    assert {score.symbol for score in evaluate_all(tmp_path)} == {"SPY"}


def test_the_report_states_how_many_cleared() -> None:
    text = report(
        [
            ModelScore("ridge", "SPY", 100, -7.0, -9.0, 0.3, -50.0, 2.0),
            ModelScore("average", "SPY", 100, -6.0, -8.0, 0.3, -40.0, 2.0),
        ]
    )

    assert "0 of 2" in text
    assert "SPY" in text


def test_an_empty_run_says_so_rather_than_printing_a_blank_table() -> None:
    assert report([]) == "no instrument produced a scored model"
