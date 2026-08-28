import polars as pl


def calculate_realized_volatility(df: pl.DataFrame, log_ret_col: str, window: int) -> pl.DataFrame:
    """Calculate realized volatility as standard deviation of log returns."""
    return df.with_columns(
        pl.col(log_ret_col).rolling_std(window_size=window).alias(f"realized_vol_{window}")
    )


def calculate_realized_variance(df: pl.DataFrame, log_ret_col: str, window: int) -> pl.DataFrame:
    """Calculate realized variance."""
    return df.with_columns(
        pl.col(log_ret_col).rolling_var(window_size=window).alias(f"realized_var_{window}")
    )


def calculate_har_inputs(df: pl.DataFrame, rv_col: str) -> pl.DataFrame:
    """Calculate HAR (Heterogeneous Auto-Regressive) inputs: daily, weekly, monthly."""
    # Daily is rv_col itself (at t-1)
    # Weekly is average of last 5 days
    # Monthly is average of last 22 days
    return df.with_columns(
        pl.col(rv_col).shift(1).alias("har_d"),
        pl.col(rv_col).shift(1).rolling_mean(window_size=5).alias("har_w"),
        pl.col(rv_col).shift(1).rolling_mean(window_size=22).alias("har_m"),
    )
