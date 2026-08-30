from collections.abc import Callable
from datetime import datetime, timedelta
from typing import Any, cast

import polars as pl


def walk_forward_validation(
    df: pl.DataFrame,
    time_col: str,
    train_size: timedelta,
    test_size: timedelta,
    step_size: timedelta,
    model_factory: Callable[[], Any],
    eval_func: Callable[[Any, pl.DataFrame], dict[str, Any]],
) -> list[dict[str, Any]]:
    """
    Perform walk-forward validation.
    """
    results: list[dict[str, Any]] = []
    start_time = cast(datetime, df[time_col].min())
    end_time = cast(datetime, df[time_col].max())

    if start_time is None or end_time is None:
        return []

    current_train_end = start_time + train_size

    while current_train_end + test_size <= end_time:
        train_start = current_train_end - train_size
        test_end = current_train_end + test_size

        train_set = df.filter(
            (pl.col(time_col) >= train_start) & (pl.col(time_col) < current_train_end)
        )
        test_set = df.filter(
            (pl.col(time_col) >= current_train_end) & (pl.col(time_col) < test_end)
        )

        if not train_set.is_empty() and not test_set.is_empty():
            model = model_factory()
            model.fit(train_set)
            fold_results = eval_func(model, test_set)
            results.append(
                {"train_end": current_train_end, "test_end": test_end, "metrics": fold_results}
            )

        current_train_end += step_size

    return results
