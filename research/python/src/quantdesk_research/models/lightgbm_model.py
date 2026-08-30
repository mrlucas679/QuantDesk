from typing import Any, Literal

import lightgbm as lgb
import numpy as np
from numpy.typing import NDArray

from quantdesk_research.resource_governor import get_resource_governor


class LightGBMModel:
    def __init__(self, params: dict[str, Any] | None = None, seed: int = 42) -> None:
        gov = get_resource_governor()
        gov.get_duckdb_config()  # Use similar thread limit

        self.params = params or {
            "objective": "regression",
            "metric": "rmse",
            "verbosity": -1,
            "boosting_type": "gbdt",
            "random_state": seed,
            "learning_rate": 0.05,
            "num_leaves": 31,
            "feature_fraction": 0.9,
            "n_jobs": gov.get_worker_count(),  # Enforce resource governance
            "device": "cpu",  # P1 Requirement: No GPU required
        }
        self.model: lgb.Booster | None = None

    def train(
        self,
        x_train: NDArray[np.float64],
        y_train: NDArray[np.float64],
        x_valid: NDArray[np.float64] | None = None,
        y_valid: NDArray[np.float64] | None = None,
        feature_names: list[str] | Literal["auto"] = "auto",
    ) -> None:
        gov = get_resource_governor()
        if not gov.check_limits():
            raise RuntimeWarning("Resource limits reached, training may fail.")

        train_data = lgb.Dataset(x_train, label=y_train, feature_name=feature_names)
        valid_sets: list[lgb.Dataset] = []
        if x_valid is not None and y_valid is not None:
            valid_data = lgb.Dataset(
                x_valid, label=y_valid, feature_name=feature_names, reference=train_data
            )
            valid_sets = [valid_data]

        self.model = lgb.train(
            self.params,
            train_data,
            num_boost_round=1000,
            valid_sets=valid_sets,
            callbacks=[lgb.early_stopping(stopping_rounds=50)] if valid_sets else [],
        )

    def predict(self, features: NDArray[np.float64]) -> NDArray[np.float64]:
        if self.model is None:
            raise ValueError("Model not trained")
        return np.asarray(self.model.predict(features), dtype=np.float64)

    def get_feature_importance(self) -> dict[str, float]:
        if self.model is None:
            raise ValueError("Model not trained")
        importance = self.model.feature_importance(importance_type="gain")
        names = self.model.feature_name()
        return dict(zip(names, importance.astype(float)))
