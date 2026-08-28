import polars as pl


def calculate_order_book_imbalance(
    df: pl.DataFrame, bid_vol_col: str, ask_vol_col: str
) -> pl.DataFrame:
    """Calculate order book imbalance."""
    return df.with_columns(
        (
            (pl.col(bid_vol_col) - pl.col(ask_vol_col))
            / (pl.col(bid_vol_col) + pl.col(ask_vol_col) + 1e-9)
        ).alias("obi")
    )


def calculate_relative_spread(df: pl.DataFrame, bid_col: str, ask_col: str) -> pl.DataFrame:
    """Calculate relative bid-ask spread."""
    mid = (pl.col(bid_col) + pl.col(ask_col)) / 2
    return df.with_columns(
        ((pl.col(ask_col) - pl.col(bid_col)) / (mid + 1e-9)).alias("relative_spread")
    )
