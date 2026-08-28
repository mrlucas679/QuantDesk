import polars as pl


def calculate_cross_asset_correlation(
    df: pl.DataFrame, asset_a_ret: str, asset_b_ret: str, window: int
) -> pl.DataFrame:
    """Calculate rolling correlation between two assets."""
    return df.with_columns(
        pl.rolling_corr(pl.col(asset_a_ret), pl.col(asset_b_ret), window_size=window).alias(
            f"corr_{asset_a_ret}_{asset_b_ret}_{window}"
        )
    )


def calculate_relative_strength(
    df: pl.DataFrame, asset_a_price: str, asset_b_price: str
) -> pl.DataFrame:
    """Calculate relative strength of asset A vs asset B."""
    return df.with_columns(
        (pl.col(asset_a_price) / pl.col(asset_b_price)).alias(
            f"relative_strength_{asset_a_price}_{asset_b_price}"
        )
    )
