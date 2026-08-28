import polars as pl


def calculate_ema_slope(df: pl.DataFrame, price_col: str, window: int) -> pl.DataFrame:
    """Calculate the slope of an Exponential Moving Average."""
    ema = pl.col(price_col).ewm_mean(span=window, adjust=False)
    return df.with_columns((ema - ema.shift(1)).alias(f"ema_slope_{window}"))


def calculate_moving_return(df: pl.DataFrame, price_col: str, window: int) -> pl.DataFrame:
    """Calculate moving return over a window."""
    return df.with_columns(
        (pl.col(price_col) / pl.col(price_col).shift(window) - 1).alias(f"moving_return_{window}")
    )


def calculate_breakout(df: pl.DataFrame, price_col: str, window: int) -> pl.DataFrame:
    """Calculate distance from high/low over a window."""
    high = pl.col(price_col).rolling_max(window_size=window)
    low = pl.col(price_col).rolling_min(window_size=window)
    return df.with_columns(
        ((pl.col(price_col) - low) / (high - low + 1e-9)).alias(f"breakout_score_{window}")
    )
