import lightgbm as lgb

from quantdesk_research.resource_governor import get_resource_governor


class LightGBMModel:
    def __init__(self, params=None, seed: int = 42):
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
        self.model = None

    def train(self, X_train, y_train, X_valid=None, y_valid=None, feature_names=None):
        gov = get_resource_governor()
        if not gov.check_limits():
            raise RuntimeWarning("Resource limits reached, training may fail.")

        train_data = lgb.Dataset(X_train, label=y_train, feature_name=feature_names)
        valid_sets = []
        if X_valid is not None and y_valid is not None:
            valid_data = lgb.Dataset(
                X_valid, label=y_valid, feature_name=feature_names, reference=train_data
            )
            valid_sets = [valid_data]

        self.model = lgb.train(
            self.params,
            train_data,
            num_boost_round=1000,
            valid_sets=valid_sets,
            callbacks=[lgb.early_stopping(stopping_rounds=50)] if valid_sets else [],
        )

    def predict(self, X):
        if self.model is None:
            raise ValueError("Model not trained")
        return self.model.predict(X)

    def get_feature_importance(self) -> dict[str, float]:
        if self.model is None:
            raise ValueError("Model not trained")
        importance = self.model.feature_importance(importance_type="gain")
        names = self.model.feature_name()
        return dict(zip(names, importance.astype(float)))
