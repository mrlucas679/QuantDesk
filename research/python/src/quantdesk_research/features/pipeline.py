from collections.abc import Callable

import polars as pl

from quantdesk_research.contracts.feature_schema import FeatureSchema


class FeaturePipeline:
    def __init__(self, schema: FeatureSchema):
        self.schema = schema
        self.transformations: list[Callable[[pl.DataFrame], pl.DataFrame]] = []

    def add_step(self, func: Callable[[pl.DataFrame], pl.DataFrame]) -> FeaturePipeline:
        self.transformations.append(func)
        return self

    def run(self, df: pl.DataFrame) -> pl.DataFrame:
        for transform in self.transformations:
            df = transform(df)

        # Validate that all features in schema are present
        missing = [f for f in self.schema.feature_names if f not in df.columns]
        if missing:
            raise ValueError(f"Missing features after pipeline execution: {missing}")

        return df.select(self.schema.feature_names + ["timestamp", "instrument"])
