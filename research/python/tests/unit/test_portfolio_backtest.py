from __future__ import annotations

import math

import numpy as np
import pandas as pd  # type: ignore[import-untyped]
import pytest

from quantdesk_research.backtest.equity_costs import (
    BASE_COST,
    SEVERE_COST,
    STRESS_COST,
)
from quantdesk_research.backtest.portfolio import (
    evaluate_weight_schedule,
    newey_west_standard_error,
    observations_required,
)


def _frame(values: dict[str, list[float]], periods: int) -> pd.DataFrame:
    return pd.DataFrame(values, index=pd.RangeIndex(periods))


def test_cost_scenarios_are_ordered_and_commission_free() -> None:
    assert BASE_COST.round_trip_bps < STRESS_COST.round_trip_bps < SEVERE_COST.round_trip_bps
    for scenario in (BASE_COST, STRESS_COST, SEVERE_COST):
        assert scenario.commission_bps == 0.0, "Alpaca US equities are commission-free."
        assert scenario.one_way_bps == scenario.round_trip_bps / 2


def test_holding_a_constant_weight_charges_cost_once() -> None:
    periods = 10
    weights = _frame({"SPY": [1.0] * periods}, periods)
    returns = _frame({"SPY": [0.0] * periods}, periods)

    result = evaluate_weight_schedule(weights, returns, BASE_COST, 1, 0.05)

    # Turnover is one unit on the opening session and zero thereafter, so the whole schedule
    # is charged exactly one side of the round trip.
    assert result.average_daily_turnover == pytest.approx(1.0 / periods)
    total_cost = -result.mean_daily_net_bps * periods
    assert total_cost == pytest.approx(BASE_COST.one_way_bps, rel=1e-9)


def test_daily_flipping_is_charged_every_session() -> None:
    periods = 10
    weights = _frame({"SPY": [1.0 if i % 2 else -1.0 for i in range(periods)]}, periods)
    returns = _frame({"SPY": [0.0] * periods}, periods)

    result = evaluate_weight_schedule(weights, returns, BASE_COST, 1, 0.05)

    # Every session flips a unit position, which trades two units of notional.
    assert result.average_daily_turnover == pytest.approx(1.9)
    assert result.mean_daily_net_bps < -BASE_COST.one_way_bps


def test_gross_return_is_the_weighted_sum_of_asset_returns() -> None:
    weights = _frame({"SPY": [0.5, 0.5], "QQQ": [0.5, 0.5]}, 2)
    returns = _frame({"SPY": [0.010, 0.020], "QQQ": [0.030, 0.040]}, 2)

    result = evaluate_weight_schedule(weights, returns, BASE_COST, 1, 0.05)

    assert result.mean_daily_gross_bps == pytest.approx(250.0)


def test_stress_scenario_never_scores_above_base() -> None:
    rng = np.random.default_rng(20260831)
    periods = 300
    weights = _frame({"SPY": list(rng.normal(0, 1, periods))}, periods)
    returns = _frame({"SPY": list(rng.normal(0.0005, 0.01, periods))}, periods)

    base = evaluate_weight_schedule(weights, returns, BASE_COST, 1, 0.05)
    stress = evaluate_weight_schedule(weights, returns, STRESS_COST, 1, 0.05)

    assert stress.mean_daily_net_bps < base.mean_daily_net_bps


def test_newey_west_widens_the_error_for_autocorrelated_returns() -> None:
    rng = np.random.default_rng(7)
    innovations = rng.normal(0, 1, 2000)
    persistent = np.zeros(2000)
    for index in range(1, 2000):
        persistent[index] = 0.8 * persistent[index - 1] + innovations[index]

    independent_error = float(persistent.std(ddof=1)) / math.sqrt(len(persistent))
    hac_error = newey_west_standard_error(persistent, holding_days=10)

    assert hac_error > independent_error


def test_observations_required_is_undefined_for_a_non_positive_edge() -> None:
    assert observations_required(-0.001, 0.0005, 500, 0.05) == -1
    assert observations_required(0.0, 0.0005, 500, 0.05) == -1


def test_observations_required_scales_with_the_inverse_square_of_the_edge() -> None:
    halved = observations_required(0.0005, 0.0002, 400, 0.05)
    full = observations_required(0.0010, 0.0002, 400, 0.05)

    # Both values are rounded up to whole sessions, so allow for the ceiling on each side.
    assert abs(halved - full * 4) <= 4


def test_alpha_outside_the_open_unit_interval_is_rejected() -> None:
    weights = _frame({"SPY": [1.0, 1.0]}, 2)
    returns = _frame({"SPY": [0.0, 0.0]}, 2)

    for invalid in (0.0, 0.5, 1.0, -0.1):
        with pytest.raises(ValueError, match="alpha"):
            evaluate_weight_schedule(weights, returns, BASE_COST, 1, invalid)


def test_a_schedule_shorter_than_two_sessions_is_rejected() -> None:
    weights = _frame({"SPY": [1.0]}, 1)
    returns = _frame({"SPY": [0.0]}, 1)

    with pytest.raises(ValueError, match="at least two"):
        evaluate_weight_schedule(weights, returns, BASE_COST, 1, 0.05)


def test_disjoint_symbols_are_rejected_rather_than_silently_scoring_zero() -> None:
    weights = _frame({"SPY": [1.0, 1.0]}, 2)
    returns = _frame({"QQQ": [0.01, 0.01]}, 2)

    with pytest.raises(ValueError, match="share no symbols"):
        evaluate_weight_schedule(weights, returns, BASE_COST, 1, 0.05)
