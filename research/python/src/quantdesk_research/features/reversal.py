import polars as pl


def calculate_short_term_reversal(df: pl.DataFrame, log_ret_col: str, window: int) -> pl.DataFrame:
    """Calculate short-term reversal (negative of recent returns)."""
    return df.with_columns(
        (-pl.col(log_ret_col).rolling_sum(window_size=window)).alias(f"reversal_{window}")
    )


def calculate_distance_from_mean(df: pl.DataFrame, price_col: str, window: int) -> pl.DataFrame:
    """Calculate distance from moving average."""
    ma = pl.col(price_col).rolling_mean(window_size=window)
    return df.with_columns((pl.col(price_col) / ma - 1).alias(f"dist_from_ma_{window}"))
