from typing import Any

import numpy as np
from hmmlearn import hmm  # type: ignore[import-untyped]
from loguru import logger
from numpy.typing import NDArray


class HMMModel:
    def __init__(self, n_components: int = 3, covariance_type: str = "full", seed: int = 42):
        self.n_components = n_components
        self.covariance_type = covariance_type
        self.seed = seed
        self.model = hmm.GaussianHMM(
            n_components=n_components, covariance_type=covariance_type, random_state=seed
        )
        self.is_fitted = False

    def fit(self, features: NDArray[np.float64]) -> None:
        """
        X: array-like of shape (n_samples, n_features)
        """
        try:
            self.model.fit(features)
            self.is_fitted = True
            logger.info(f"HMM fitted successfully with {self.n_components} states.")
        except Exception as e:
            logger.error(f"HMM fit failed: {e}")
            self.is_fitted = False
            raise

    def predict_states(self, features: NDArray[np.float64]) -> NDArray[np.int_]:
        if not self.is_fitted:
            raise ValueError("Model not fitted")
        return np.asarray(self.model.predict(features), dtype=np.int_)

    def get_artifact_data(self) -> dict[str, Any]:
        if not self.is_fitted:
            raise ValueError("Model not fitted")
        return {
            "n_components": self.n_components,
            "covariance_type": self.covariance_type,
            "startprob": self.model.startprob_.tolist(),
            "transmat": self.model.transmat_.tolist(),
            "means": self.model.means_.tolist(),
            "covars": self.model.covars_.tolist(),
        }
