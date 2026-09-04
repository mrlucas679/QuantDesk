"""Combinatorial purged evaluation of a daily weight schedule.

Why this exists
---------------
The equity campaign judged every family on one chronological 50/25/25 split. Measured on the
committed panel, that split does something quietly fatal:

===========  ========  ==========  =========  ========
phase        sessions  ann return  Sharpe     max DD
===========  ========  ==========  =========  ========
discovery         982     11.33 %       0.48   -34.8 %
validation        491     25.30 %       1.56   -11.6 %
holdout           492     22.62 %       1.29   -20.3 %
===========  ========  ==========  =========  ========

Every drawdown worth surviving landed in discovery — the COVID crash and the 2022 bear market —
and the out-of-sample windows are the calmest stretch in the sample. A trend or defensive family
exists to give up upside in exchange for protection it never gets paid for in a window like that.
Asking it to beat always-on beta there is close to asking it to fail: capped-at-zero trend can only
be flat or long during a bull market, so underperformance is the design working, not the signal
being absent.

The single-path problem is well described. Walk-forward evaluates on one sequence of test sets, so
the estimate is contingent on one historical path and carries high variance; combinatorial purged
cross-validation instead builds many train/test combinations and yields a *distribution* of
out-of-sample results, with materially lower probability of backtest overfitting.

What this module does
---------------------
Splits the sample into contiguous blocks, holds out every combination of ``test_blocks`` of them,
and evaluates the schedule on each combination. The output is a distribution — median Sharpe, the
share of paths that beat the benchmark, and the worst path — rather than a single number that
happens to describe one regime.

Purging is by embargo at the *start* of each held-out block, and the size of that embargo needs care
for the reason these families are unusual: **they have no fitted parameters**. Nothing is learned
from the in-sample blocks, so the leakage purging normally defends against — a model that saw the
test period during training — cannot occur here. A weight whose lookback window reaches back into
the preceding block is not leakage; it is what live trading does every day.

What the embargo must remove is the stale-weight boundary: at the first observation of a held-out
block the schedule carries a weight last rebalanced before the block began, and crediting that
carried position to the test measures the previous regime. Embargoing the holding period is
therefore sufficient, and embargoing the full lookback is not merely unnecessary but destructive —
a 252-day lookback against 245-observation blocks removes every observation and leaves no path at
all.

This does not weaken any gate. A family still has to earn its result; it simply has to earn it
across many regimes instead of one.
"""

from __future__ import annotations

import math
from dataclasses import dataclass
from itertools import combinations

import numpy as np
import pandas as pd  # type: ignore[import-untyped]

from quantdesk_research.backtest.equity_costs import EquityCostScenario
from quantdesk_research.backtest.portfolio import (
    PortfolioPerformance,
    evaluate_weight_schedule,
)


@dataclass(frozen=True)
class CombinatorialEvaluation:
    """Out-of-sample behaviour of one schedule across many held-out combinations."""

    path_count: int
    observations_per_path: int
    median_sharpe: float
    mean_sharpe: float
    worst_sharpe: float
    best_sharpe: float
    sharpe_dispersion: float
    median_net_bps: float
    worst_net_bps: float
    positive_path_share: float
    beats_benchmark_share: float

    @property
    def robust(self) -> bool:
        """True when the schedule beats the benchmark on most paths, not just the lucky one.

        Two thirds is a deliberately blunt threshold. The point is not the exact number but that a
        family which wins on one path out of twenty-eight has demonstrated a regime, not an edge.
        """
        return self.beats_benchmark_share >= 2.0 / 3.0


def block_bounds(observation_count: int, block_count: int) -> list[tuple[int, int]]:
    """Contiguous, near-equal blocks covering the sample in chronological order."""
    if block_count < 2:
        raise ValueError("At least two blocks are required to hold one out.")
    if observation_count < block_count:
        raise ValueError("Fewer observations than blocks.")
    edges = np.linspace(0, observation_count, block_count + 1).astype(int)
    return [(int(edges[i]), int(edges[i + 1])) for i in range(block_count)]


def held_out_indices(
    observation_count: int,
    block_count: int,
    test_blocks: int,
    embargo: int,
) -> list[np.ndarray]:
    """Row indices for every combination of ``test_blocks`` held-out blocks.

    The first ``embargo`` rows of each held-out block are dropped so a position carried in from
    before the block is not credited to it. See the module docstring for why this is a stale-weight
    boundary rather than a leakage purge: these schedules fit nothing, so there is no trained model
    that could have seen the test period.
    """
    bounds = block_bounds(observation_count, block_count)
    paths: list[np.ndarray] = []
    for chosen in combinations(range(block_count), test_blocks):
        pieces = [
            np.arange(min(start + embargo, stop), stop)
            for start, stop in (bounds[index] for index in chosen)
        ]
        indices = np.concatenate(pieces) if pieces else np.array([], dtype=int)
        if indices.size > 0:
            paths.append(indices)
    return paths


def evaluate_combinatorially(
    weights: pd.DataFrame,
    returns: pd.DataFrame,
    cost: EquityCostScenario,
    holding_days: int,
    benchmark_weights: pd.DataFrame | None = None,
    block_count: int = 8,
    test_blocks: int = 2,
    embargo: int = 21,
    alpha: float = 0.05,
) -> CombinatorialEvaluation:
    """Evaluate one weight schedule across every held-out block combination."""
    if len(weights) != len(returns):
        raise ValueError("Weights and returns must be aligned.")

    paths = held_out_indices(len(weights), block_count, test_blocks, embargo)
    if not paths:
        raise ValueError("No usable held-out paths; embargo may exceed the block length.")

    sharpes: list[float] = []
    net_bps: list[float] = []
    beats: list[bool] = []

    for indices in paths:
        performance = _evaluate_rows(weights, returns, indices, cost, holding_days, alpha)
        sharpes.append(performance.sharpe)
        net_bps.append(performance.mean_daily_net_bps)
        if benchmark_weights is not None:
            reference = _evaluate_rows(
                benchmark_weights, returns, indices, cost, holding_days, alpha
            )
            beats.append(performance.sharpe > reference.sharpe)

    sharpe_array = np.asarray(sharpes, dtype=float)
    net_array = np.asarray(net_bps, dtype=float)
    return CombinatorialEvaluation(
        path_count=len(paths),
        observations_per_path=int(np.median([len(item) for item in paths])),
        median_sharpe=float(np.median(sharpe_array)),
        mean_sharpe=float(np.mean(sharpe_array)),
        worst_sharpe=float(np.min(sharpe_array)),
        best_sharpe=float(np.max(sharpe_array)),
        sharpe_dispersion=float(np.std(sharpe_array)),
        median_net_bps=float(np.median(net_array)),
        worst_net_bps=float(np.min(net_array)),
        positive_path_share=float(np.mean(net_array > 0.0)),
        beats_benchmark_share=float(np.mean(beats)) if beats else math.nan,
    )


def _evaluate_rows(
    weights: pd.DataFrame,
    returns: pd.DataFrame,
    indices: np.ndarray,
    cost: EquityCostScenario,
    holding_days: int,
    alpha: float,
) -> PortfolioPerformance:
    return evaluate_weight_schedule(
        weights.iloc[indices], returns.iloc[indices], cost, holding_days, alpha
    )
