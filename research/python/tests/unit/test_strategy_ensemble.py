from pathlib import Path

import numpy as np
import pandas as pd

from quantdesk_research.experiments.prospective_campaign import ProspectiveCampaign
from quantdesk_research.experiments.strategy_ensemble import (
    build_strategy_frame,
    evaluate_prospective_strategy,
    non_overlapping,
)


def test_non_overlapping_skips_signals_inside_holding_period() -> None:
    signal = np.asarray([True, True, True, False, True], dtype=np.bool_)
    returns = np.asarray([0.1, 0.2, 0.3, 0.4, 0.5], dtype=np.float64)

    selected = non_overlapping(signal, returns, horizon=3)

    assert selected.tolist() == [0.1, 0.5]


def test_non_overlapping_returns_empty_when_strategy_abstains() -> None:
    signal = np.asarray([False, False], dtype=np.bool_)
    returns = np.asarray([0.1, -0.1], dtype=np.float64)

    assert non_overlapping(signal, returns, horizon=2).size == 0


def test_prospective_evaluation_applies_costs_and_multiplicity() -> None:
    campaign = ProspectiveCampaign.load(
        Path(__file__).parents[2] / "configs" / "prospective_strategy_campaign.json"
    )
    frame = pd.DataFrame(
        {
            "donchian_breakout": [True] * 120,
            "target_return": np.asarray([0.006, 0.0062] * 60, dtype=np.float64),
        }
    )

    result = evaluate_prospective_strategy(
        "donchian_breakout", frame, 1, campaign, comparison_count=32
    )

    assert result.trade_count == 120
    assert result.mean_net_bps == 1.0
    assert result.passed is True


def test_literature_signals_use_only_information_available_at_decision_time() -> None:
    bar_count = 8_100
    bars = [
        {
            "t": "2022-01-01T00:00:00+00:00",
            "o": float(index + 1),
            "h": float(index + 1),
            "l": float(index + 1),
            "c": float(index + 1),
            "v": 1.0,
        }
        for index in range(bar_count)
    ]

    baseline = build_strategy_frame(bars, horizon_bars=1)
    mutated = [dict(bar) for bar in bars]
    mutated[-1]["c"] = 1.0
    changed = build_strategy_frame(mutated, horizon_bars=1)

    signal_columns = (
        "weekly_time_series_momentum",
        "four_week_time_series_momentum",
        "dual_horizon_momentum",
        "four_week_breakout",
    )
    for column in signal_columns:
        assert baseline[column].iloc[:-1].equals(changed[column].iloc[:-1])
