from typing import Any

from pydantic import BaseModel


class FeatureSchema(BaseModel):
    schema_version: str
    feature_names: list[str]
    dtypes: dict[str, str]
    normalization: dict[str, Any]
    lookback_periods: int
    source_requirements: list[str]
    feature_hash: str

    def validate_columns(self, columns: list[str]) -> bool:
        return all(f in columns for f in self.feature_names)
