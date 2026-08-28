import numpy as np
from loguru import logger
from sklearn.isotonic import IsotonicRegression  # type: ignore[import-untyped]


class ProbabilityCalibrator:
    """
    Calibrates model output probabilities using Isotonic Regression.
    """

    def __init__(self):
        self.ir = IsotonicRegression(out_of_bounds="clip")
        self.is_fitted = False

    def fit(self, probs: np.ndarray, y_true: np.ndarray):
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

    def calibrate(self, probs: np.ndarray) -> np.ndarray:
        if not self.is_fitted:
            return probs
        return self.ir.transform(probs)

    def export_params(self) -> dict:
        if not self.is_fitted:
            return {}
        return {"x": self.ir.X_thresholds_.tolist(), "y": self.ir.y_thresholds_.tolist()}
