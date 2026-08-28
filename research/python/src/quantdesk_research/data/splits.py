from datetime import timedelta
from typing import Any, cast

import polars as pl
from loguru import logger


def chronological_split(
    df: pl.DataFrame, time_col: str, train_end: str, test_start: str
) -> tuple[pl.DataFrame, pl.DataFrame]:
    """
    Split data chronologically.
    train_end: exclusive
    test_start: inclusive
    """
    train = df.filter(
        pl.col(time_col) < pl.from_epoch(train_end)
        if isinstance(train_end, int)
        else pl.col(time_col) < pl.lit(train_end)
    )
    test = df.filter(
        pl.col(time_col) >= pl.from_epoch(test_start)
        if isinstance(test_start, int)
        else pl.col(time_col) >= pl.lit(test_start)
    )

    logger.info(f"Chronological split: {len(train)} train, {len(test)} test")
    return train, test


def get_walk_forward_folds(
    df: pl.DataFrame, time_col: str, train_window_days: int, test_window_days: int, step_days: int
):
    """Generate (train, test) folds for walk-forward validation."""
    # Simplified version using days
    min_time = cast(Any, df[time_col].min())
    max_time = cast(Any, df[time_col].max())

    current_train_start = min_time
    while True:
        train_end = current_train_start + timedelta(days=train_window_days)
        test_end = train_end + timedelta(days=test_window_days)

        if test_end > max_time:
            break

        train_fold = df.filter(
            (pl.col(time_col) >= current_train_start) & (pl.col(time_col) < train_end)
        )
        test_fold = df.filter((pl.col(time_col) >= train_end) & (pl.col(time_col) < test_end))

        yield train_fold, test_fold

        current_train_start += timedelta(days=step_days)
