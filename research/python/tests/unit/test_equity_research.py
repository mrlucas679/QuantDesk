from __future__ import annotations

from typing import Any

import numpy as np
import pandas as pd  # type: ignore[import-untyped]
import pytest

from quantdesk_research.backtest.equity_costs import BASE_COST, COST_SCENARIOS
from quantdesk_research.experiments.equity_research import (
    CANDIDATES,
    build_daily_features,
    candidate_returns,
    chronological_phase,
    lower_mean_bound,
)


def test_cost_scenarios_match_alpacas_commission_free_equity_schedule() -> None:
    """Pin the corrected round-trip costs for penny-spread US ETFs.

    This test previously asserted a 25/35/50 bps ladder. Those figures were roughly eight times
    the achievable cost for SPY, QQQ, IWM, and DIA on a commission-free venue, and they silently
    rejected any candidate whose real edge was smaller than the modelling error. The derivation
    of the corrected ladder is in ``quantdesk_research.backtest.equity_costs``.
    """
    assert BASE_COST.round_trip_bps == 5.0
    assert [scenario.round_trip_bps for scenario in COST_SCENARIOS] == [5.0, 10.0, 20.0]
    assert BASE_COST.net_return(0.01) == pytest.approx(0.0095)
    for scenario in COST_SCENARIOS:
        assert scenario.commission_bps == 0.0


def test_all_twenty_candidates_are_unique_and_preregistered() -> None:
    assert len(CANDIDATES) == 20
    assert len({candidate.slug for candidate in CANDIDATES}) == 20
    assert [candidate.number for candidate in CANDIDATES] == list(range(1, 21))


def test_daily_features_do_not_change_when_a_future_bar_changes() -> None:
    bars = [_daily_bar(index) for index in range(260)]
    original = build_daily_features(bars, "SPY")
    bars[-1]["c"] = 10_000.0
    changed = build_daily_features(bars, "SPY")

    feature_columns = [column for column in original if column.startswith("prior_")]
    assert original.loc[220, feature_columns].equals(changed.loc[220, feature_columns])


def test_candidate_signal_uses_prior_data_but_current_open_to_close_return() -> None:
    bars = [_daily_bar(index) for index in range(260)]
    frame = build_daily_features(bars, "SPY")
    returns = candidate_returns(6, frame, pd.DataFrame())

    assert not returns.empty
    assert np.isfinite(returns["gross_return"]).all()


def test_chronological_validation_and_holdout_are_disjoint() -> None:
    returns = pd.DataFrame(
        {"date": pd.date_range("2020-01-01", periods=100).date, "gross_return": np.arange(100)}
    )

    validation = chronological_phase(returns, "validation")
    holdout = chronological_phase(returns, "holdout")

    assert len(validation) == 25
    assert len(holdout) == 25
    assert validation["date"].max() < holdout["date"].min()


def test_multiple_testing_confidence_bound_is_more_conservative() -> None:
    values = np.asarray([0.01, 0.02, -0.005, 0.015] * 20, dtype=np.float64)

    ordinary = lower_mean_bound(values, 0.05)
    corrected = lower_mean_bound(values, 0.05 / 20)

    assert corrected < ordinary


def _daily_bar(index: int) -> dict[str, Any]:
    price = 100.0 + index * 0.1
    timestamp = pd.Timestamp("2020-01-02", tz="UTC") + pd.Timedelta(days=index)
    return {
        "t": timestamp.isoformat().replace("+00:00", "Z"),
        "o": price,
        "h": price + 1,
        "l": price - 1,
        "c": price + 0.05,
        "v": 1_000 + index,
        "n": 100,
        "vw": price,
    }
