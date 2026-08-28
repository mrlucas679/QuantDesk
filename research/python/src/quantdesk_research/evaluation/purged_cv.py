from datetime import datetime, timedelta
from typing import Any, cast

import polars as pl


def purge_data(
    df: pl.DataFrame, train_end: datetime, test_start: datetime, embargo_period: timedelta
) -> pl.DataFrame:
    """
    Remove data that might overlap with the test set.
    """
    embargo_end = test_start + embargo_period
    # For purged CV, we remove samples from train that end after test_start - max_horizon
    # and samples from train that start before test_end + embargo
    return df.filter((pl.col("timestamp") < train_end) | (pl.col("timestamp") > embargo_end))


def get_purged_train_test(
    df: pl.DataFrame,
    train_indices: list[int],
    test_indices: list[int],
    timestamp_col: str,
    max_horizon: timedelta,
    embargo_pct: float = 0.01,
) -> tuple[pl.DataFrame, pl.DataFrame]:
    """
    Marcos Lopez de Prado style purging and embargo.
    """
    timestamps = df.select(timestamp_col).to_series()

    test_start = timestamps[test_indices[0]]
    test_end = timestamps[test_indices[-1]]

    # 1. Purge: remove observations from train set that overlap with test set
    # An observation at T overlaps if it ends at T + max_horizon
    # So we remove T from train if T + max_horizon > test_start AND T < test_end

    purged_train_indices = []
    for idx in train_indices:
        t = timestamps[idx]
        if not (t + max_horizon > test_start and t < test_end):
            purged_train_indices.append(idx)

    # 2. Embargo: remove observations from train set that occur immediately AFTER the test set
    if timestamps.is_empty():
        return df.clear(), df.gather(test_indices)

    t_max = cast(Any, timestamps.max())
    t_min = cast(Any, timestamps.min())
    diff = cast(timedelta, t_max - t_min)
    embargo_period = timedelta(seconds=int(diff.total_seconds() * embargo_pct))
    final_train_indices = []
    for idx in purged_train_indices:
        t = timestamps[idx]
        if not (t > test_end and t < test_end + embargo_period):
            final_train_indices.append(idx)

    return df.gather(final_train_indices), df.gather(test_indices)


class CombinatorialPurgedCV:
    """
    CPCV implementation for backtest evaluation.
    """

    def __init__(self, n_folds: int = 10, n_test_folds: int = 2):
        self.n_folds = n_folds
        self.n_test_folds = n_test_folds

    def split(self, df: pl.DataFrame):
        # Implementation of combinatorial splits
        # For now, return simple chronological folds as a fallback
        n = len(df)
        fold_size = n // self.n_folds
        for i in range(self.n_folds - self.n_test_folds):
            train_idx = list(
                range(i * fold_size, (i + self.n_folds - self.n_test_folds) * fold_size)
            )
            test_idx = list(
                range(
                    (i + self.n_folds - self.n_test_folds) * fold_size,
                    min((i + self.n_folds) * fold_size, n),
                )
            )
            yield train_idx, test_idx
