import polars as pl
from loguru import logger


def as_of_join(
    left: pl.DataFrame, right: pl.DataFrame, on: str, by: list[str] | None = None
) -> pl.DataFrame:
    """
    Perform a point-in-time (as-of) join.
    Ensures that for each row in 'left', we only see data from 'right' where
    right_timestamp <= left_timestamp.
    """
    # Assuming 'on' is the timestamp column
    # Ensure both are sorted by 'on'
    left = left.sort(on)
    right = right.sort(on)

    return left.join_asof(right, on=on, by=by, strategy="backward")


def validate_no_lookahead(
    df: pl.DataFrame, event_time_col: str, available_time_col: str
) -> bool:
    """
    Hard failure if event_time > available_time in any row.
    """
    violations = df.filter(pl.col(event_time_col) > pl.col(available_time_col))
    if not violations.is_empty():
        logger.error(
            f"Point-in-time violation detected: {len(violations)} rows have event_time > available_time"
        )
        raise ValueError("LOOKAHEAD_DETECTED")
    return True
