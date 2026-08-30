from pathlib import Path

import polars as pl


def write_parquet(df: pl.DataFrame, path: Path) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    df.write_parquet(path)


def read_parquet(path: Path) -> pl.DataFrame:
    return pl.read_parquet(path)


def scan_parquet(path: Path) -> pl.LazyFrame:
    return pl.scan_parquet(path)
