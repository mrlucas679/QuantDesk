from typing import Any

import numpy as np
from loguru import logger
from numpy.typing import NDArray
from sklearn.isotonic import IsotonicRegression  # type: ignore[import-untyped]


class ProbabilityCalibrator:
    """
    Calibrates model output probabilities using Isotonic Regression.
    """

    def __init__(self) -> None:
        self.ir: Any = IsotonicRegression(out_of_bounds="clip")
        self.is_fitted = False

    def fit(self, probs: NDArray[np.float64], y_true: NDArray[np.int_]) -> None:
        """
        probs: model predicted probabilities
        y_true: binary labels (0 or 1)
        """
        try:
            self.ir.fit(probs, y_true)
            self.is_fitted = True
        except Exception as e:
            logger.error(f"Calibration fit failed: {e}")
            raise

    def calibrate(self, probs: NDArray[np.float64]) -> NDArray[np.float64]:
        if not self.is_fitted:
            return probs
        return np.asarray(self.ir.transform(probs), dtype=np.float64)

    def export_params(self) -> dict[str, list[float]]:
        if not self.is_fitted:
            return {}
        return {
            "x": np.asarray(self.ir.X_thresholds_, dtype=np.float64).tolist(),
            "y": np.asarray(self.ir.y_thresholds_, dtype=np.float64).tolist(),
        }
