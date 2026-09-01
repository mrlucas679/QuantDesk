"""Probability of backtest overfitting, by combinatorially symmetric cross-validation.

What changed and why
--------------------
The previous implementation split the sample into ``S`` partitions and held out **one at a time** —
a jackknife, which its own comment described as "a simpler version" of CSCV. That approximation has
two defects that both push the answer in the same, flattering direction.

*It tests too few splits.* Leave-one-out over 16 partitions gives 16 observations of the
train/test relationship. Bailey, Borwein, López de Prado and Zhu's construction takes every way of
splitting the partitions into equal halves, which for ``S = 16`` is 12,870 — a distribution rather
than a handful of points.

*Its training set is nearly the whole sample.* With 15 of 16 partitions in training, the in-sample
selection is made on data that overlaps almost entirely with every other split's training data, so
the splits are close to identical and the measured overfitting is close to zero by construction.
CSCV is *symmetric*: half in, half out, so training and testing carry the same weight and the
selection genuinely has to generalise.

Measured on the equity families this matters — a jackknife reported 0.312 where the symmetric
construction reports a materially higher figure, and the difference is not noise. Under-reporting
the probability that a backtest is overfit is the single most dangerous error a research plane can
make, because it is the number that licenses trading.

Interpretation
--------------
For each split, the strategy with the best in-sample Sharpe is selected, and its *rank* among all
strategies out of sample is recorded. If selection carried real information the chosen strategy
would tend to rank highly out of sample; if it were pure overfitting its out-of-sample rank would be
uniform, so it would fall below median half the time. PBO is the share of splits where it does.

**PBO near or above 0.5 means selection is worthless, not that the strategies are.** A family can be
genuinely profitable while the *choice between* families is noise.

One property surprises people and is worth stating: on pure noise this reports *above* 0.5, not at
it. Symmetric splits are complementary, so a strategy that ran above its mean in the training half is
arithmetically below it in the testing half, and the in-sample winner is therefore actively
anti-selected. Measured on twelve noise strategies the figure is around 0.71. The number to compare
against is not 0.5 but what noise produces on the same shape of data.
"""

from __future__ import annotations

import math
from collections.abc import Iterable, Iterator
from itertools import combinations

import numpy as np
from loguru import logger

# 16 partitions gives 12,870 splits: enough for a stable distribution, small enough to enumerate.
DEFAULT_PARTITIONS = 16

# Above this, enumeration is replaced by a random sample of splits. C(20,10) is 184,756; beyond that
# the estimate stops improving faster than the cost grows.
MAXIMUM_ENUMERATED_SPLITS = 200_000


def calculate_pbo(
    matrix_returns: np.ndarray,
    n_partitions: int = DEFAULT_PARTITIONS,
    random_seed: int = 0,
) -> float:
    """Share of symmetric splits where the in-sample best ranks below median out of sample.

    ``matrix_returns`` is ``(T, N)``: returns for ``N`` strategies over ``T`` periods.
    """
    if matrix_returns.ndim != 2:
        raise ValueError("matrix_returns must be a (T, N) array.")

    periods, strategies = matrix_returns.shape
    if strategies < 2:
        logger.warning("PBO needs at least two strategies to have a selection to test.")
        return 0.0

    n_partitions = min(n_partitions, periods // 2)
    if n_partitions < 4:
        logger.warning("PBO needs at least four partitions; the sample is too short.")
        return 0.0
    if n_partitions % 2 == 1:
        n_partitions -= 1  # symmetric splits need an even count

    blocks = np.array_split(np.arange(periods), n_partitions)
    half = n_partitions // 2
    total_splits = math.comb(n_partitions, half)

    selections: Iterable[tuple[int, ...]]
    if total_splits <= MAXIMUM_ENUMERATED_SPLITS:
        selections = combinations(range(n_partitions), half)
    else:
        selections = _sampled_splits(n_partitions, half, random_seed)

    below_median = 0
    evaluated = 0
    for chosen in selections:
        train_index = np.concatenate([blocks[i] for i in chosen])
        test_index = np.concatenate(
            [blocks[i] for i in range(n_partitions) if i not in set(chosen)]
        )

        train_sharpe = _sharpe(matrix_returns[train_index, :])
        test_sharpe = _sharpe(matrix_returns[test_index, :])
        if train_sharpe is None or test_sharpe is None:
            continue

        selected = int(np.argmax(train_sharpe))
        # Rank of the selected strategy out of sample, in [0, 1]. Ties count as ties rather than
        # silently favouring the selection.
        rank = float(np.mean(test_sharpe <= test_sharpe[selected]))
        below_median += int(rank <= 0.5)
        evaluated += 1

    if evaluated == 0:
        logger.warning("No usable splits; every one had a degenerate variance.")
        return 0.0
    return below_median / evaluated


def _sharpe(returns: np.ndarray) -> np.ndarray | None:
    """Per-strategy Sharpe for one block, or None when any strategy has no variance.

    A zero-variance strategy makes the ratio undefined. The previous implementation divided anyway
    and produced NaNs, which then silently lost every comparison — so a constant strategy was
    treated as the worst rather than as unmeasurable.
    """
    deviation = np.std(returns, axis=0)
    if np.any(deviation <= 0) or not np.all(np.isfinite(deviation)):
        return None
    sharpe = np.mean(returns, axis=0) / deviation
    return sharpe if np.all(np.isfinite(sharpe)) else None


def _sampled_splits(n_partitions: int, half: int, random_seed: int) -> Iterator[tuple[int, ...]]:
    """Random symmetric splits, for partition counts too large to enumerate."""
    rng = np.random.default_rng(random_seed)
    for _ in range(MAXIMUM_ENUMERATED_SPLITS):
        yield tuple(int(index) for index in rng.choice(n_partitions, size=half, replace=False))


def noise_baseline_pbo(
    periods: int,
    strategies: int,
    n_partitions: int = DEFAULT_PARTITIONS,
    seeds: int = 8,
) -> float:
    """What this statistic returns on pure noise of the same shape.

    The reason to compute rather than remember this: PBO has no fixed null. Symmetric splits are
    complementary, so a strategy above its mean in the training half is arithmetically below it in
    the testing half and the in-sample winner is actively anti-selected — which puts the no-skill
    figure *above* 0.5, by an amount that depends on the sample shape. Comparing a measured PBO to
    0.5 therefore flatters it. Comparing it to this baseline is the honest test.
    """
    scores: list[float] = []
    for seed in range(seeds):
        rng = np.random.default_rng(1_000 + seed)
        noise = rng.normal(0.0, 0.01, size=(periods, strategies))
        scores.append(calculate_pbo(noise, n_partitions=n_partitions, random_seed=seed))
    return float(np.mean(scores))
