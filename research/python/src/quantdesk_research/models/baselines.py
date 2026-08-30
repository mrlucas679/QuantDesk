from abc import ABC, abstractmethod

import numpy as np
import polars as pl


class BaselineModel(ABC):
    """Base for simple baseline models."""

    def fit(self, X: pl.DataFrame, y: pl.Series) -> None:
        """Fit state when required; stateless baselines may keep the default."""

    @abstractmethod
    def predict(self, X: pl.DataFrame) -> np.ndarray:
        """Return one prediction for each input row."""


class HistoricalMeanBaseline(BaselineModel):
    """Predicts the historical mean of the target."""

    def __init__(self) -> None:
        self.mean = 0.0

    def fit(self, X: pl.DataFrame, y: pl.Series) -> None:
        mean = y.mean()
        if not isinstance(mean, (int, float)):
            raise TypeError("Historical mean baseline requires a numeric target series")
        self.mean = float(mean)

    def predict(self, X: pl.DataFrame) -> np.ndarray:
        return np.full(len(X), self.mean)


class NaiveNoChangeBaseline(BaselineModel):
    """Predicts the last observed value (t-1)."""

    def __init__(self, target_col: str):
        self.target_col = target_col

    def predict(self, X: pl.DataFrame) -> np.ndarray:
        # Assumes the last target value is available as a feature
        if self.target_col in X.columns:
            return X[self.target_col].to_numpy()
        raise ValueError(
            f"Target column {self.target_col} not found in features for Naive baseline"
        )
