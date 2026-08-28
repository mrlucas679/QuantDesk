import polars as pl


def calculate_log_returns(df: pl.DataFrame, price_col: str, horizon: int = 1) -> pl.DataFrame:
    return df.with_columns(pl.col(price_col).log().diff(horizon).alias(f"log_return_{horizon}"))


def calculate_simple_returns(df: pl.DataFrame, price_col: str, horizon: int = 1) -> pl.DataFrame:
    return df.with_columns(
        (pl.col(price_col) / pl.col(price_col).shift(horizon) - 1).alias(f"simple_return_{horizon}")
    )


def calculate_forward_labels(df: pl.DataFrame, price_col: str, horizons: list[int]) -> pl.DataFrame:
    cols = []
    for h in horizons:
        cols.append(
            (pl.col(price_col).shift(-h).log() - pl.col(price_col).log()).alias(
                f"label_log_ret_{h}"
            )
        )
    return df.with_columns(cols)
