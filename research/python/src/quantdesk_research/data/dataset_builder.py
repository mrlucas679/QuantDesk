import polars as pl
from loguru import logger

from quantdesk_research.data.point_in_time import validate_no_lookahead
from quantdesk_research.features.pipeline import FeaturePipeline


class DatasetBuilder:
    def __init__(self, pipeline: FeaturePipeline):
        self.pipeline = pipeline

    def build(
        self,
        raw_df: pl.DataFrame,
        event_time_col: str = "timestamp",
        available_time_col: str | None = None,
    ) -> pl.DataFrame:
        """
        Build a research dataset from raw data.
        1. Validates PIT integrity.
        2. Executes feature pipeline.
        3. Returns final feature set.
        """
        if available_time_col:
            validate_no_lookahead(raw_df, event_time_col, available_time_col)

        logger.info(f"Building dataset for {raw_df['instrument'].unique().to_list()}")

        df = self.pipeline.run(raw_df)

        # Additional validation: no NaNs in features for training
        feature_cols = self.pipeline.schema.feature_names
        null_counts = df.select(feature_cols).null_count()
        if null_counts.sum_horizontal().item() > 0:
            logger.warning(f"Dataset contains null values: {null_counts}")

        return df
