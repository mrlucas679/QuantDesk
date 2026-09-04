"""Combinatorial evaluation must widen the regimes tested without weakening any gate."""
from itertools import pairwise

import numpy as np
import pandas as pd
import pytest

from quantdesk_research.backtest.combinatorial import (
    block_bounds,
    evaluate_combinatorially,
    held_out_indices,
)
from quantdesk_research.backtest.equity_costs import BASE_COST


def _panel(sessions: int = 800, assets: int = 4) -> tuple[pd.DataFrame, pd.DataFrame]:
    rng = np.random.default_rng(7)
    returns = pd.DataFrame(
        rng.normal(0.0004, 0.01, size=(sessions, assets)),
        columns=[f"A{i}" for i in range(assets)],
    )
    weights = pd.DataFrame(1.0 / assets, index=returns.index, columns=returns.columns)
    return weights, returns


def test_blocks_cover_the_sample_exactly() -> None:
    bounds = block_bounds(100, 8)
    assert bounds[0][0] == 0
    assert bounds[-1][1] == 100
    for (_, end), (start, _) in pairwise(bounds):
        assert end == start


def test_every_combination_of_blocks_becomes_a_path() -> None:
    # 8 choose 2 is 28 distinct held-out combinations, which is the point of the method: one
    # contiguous window is one observation of a regime, not a distribution over regimes.
    paths = held_out_indices(800, block_count=8, test_blocks=2, embargo=5)
    assert len(paths) == 28


def test_the_embargo_removes_the_start_of_each_held_out_block() -> None:
    bounds = block_bounds(800, 8)
    embargo = 10
    paths = held_out_indices(800, block_count=8, test_blocks=1, embargo=embargo)
    first_block_start = bounds[0][0]
    assert paths[0][0] == first_block_start + embargo


def test_an_embargo_longer_than_a_block_is_refused_rather_than_silently_empty() -> None:
    # The failure that actually occurred: a 252-day embargo against 245-observation blocks removed
    # every observation. Returning an empty result would have looked like "no edge".
    weights, returns = _panel(sessions=800)
    with pytest.raises(ValueError, match="No usable held-out paths"):
        evaluate_combinatorially(
            weights, returns, BASE_COST, holding_days=1, block_count=8, embargo=500
        )


def test_misaligned_inputs_are_refused() -> None:
    weights, returns = _panel()
    with pytest.raises(ValueError, match="aligned"):
        evaluate_combinatorially(weights.iloc[:-5], returns, BASE_COST, holding_days=1)


def test_a_schedule_identical_to_the_benchmark_never_beats_it() -> None:
    # Guards the comparison itself: if this reported wins, the benchmark comparison is broken.
    weights, returns = _panel()
    result = evaluate_combinatorially(
        weights, returns, BASE_COST, holding_days=1, benchmark_weights=weights
    )
    assert result.beats_benchmark_share == 0.0
    assert not result.robust


def test_the_distribution_is_reported_not_just_a_point() -> None:
    weights, returns = _panel()
    result = evaluate_combinatorially(weights, returns, BASE_COST, holding_days=1)

    assert result.path_count == 28
    assert result.worst_sharpe <= result.median_sharpe <= result.best_sharpe
    assert result.sharpe_dispersion >= 0.0
    assert 0.0 <= result.positive_path_share <= 1.0
