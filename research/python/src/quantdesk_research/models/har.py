import numpy as np
from numpy.typing import NDArray


class HARModel:
    """
    Heterogeneous AutoRegressive (HAR) model for volatility.
    RV_t = c + beta_d * RV_{t-1} + beta_w * RV_{t-5:t-1} + beta_m * RV_{t-22:t-1} + epsilon_t
    """

    def __init__(self) -> None:
        self.coefficients: NDArray[np.float64] | None = None
        self.is_fitted = False

    def _prepare_features(
        self, rv: NDArray[np.float64]
    ) -> tuple[NDArray[np.float64], NDArray[np.float64]]:
        n = len(rv)
        # rv_d: RV_{t-1}
        # rv_w: average of RV over last 5 days
        # rv_m: average of RV over last 22 days

        rv_d = rv[21:-1]

        rv_w = np.array([np.mean(rv[i - 5 : i]) for i in range(22, n)])
        rv_m = np.array([np.mean(rv[i - 22 : i]) for i in range(22, n)])

        y = rv[22:]
        X = np.column_stack([np.ones(len(y)), rv_d, rv_w, rv_m])
        return np.asarray(X, dtype=np.float64), np.asarray(y, dtype=np.float64)

    def fit(self, rv: NDArray[np.float64]) -> None:
        if len(rv) < 23:
            raise ValueError("Insufficient history for HAR model")

        X, y = self._prepare_features(rv)
        # The lagged HAR features can be collinear for legitimate low-variation
        # windows. Use the deterministic minimum-norm least-squares solution.
        coefficients, _, _, _ = np.linalg.lstsq(X, y, rcond=None)
        self.coefficients = np.asarray(coefficients, dtype=np.float64)
        self.is_fitted = True

    def predict(self, rv_d: float, rv_w: float, rv_m: float) -> float:
        if not self.is_fitted or self.coefficients is None:
            raise ValueError("Model not fitted")
        return float(
            self.coefficients[0]
            + self.coefficients[1] * rv_d
            + self.coefficients[2] * rv_w
            + self.coefficients[3] * rv_m
        )

    def export_coefficients(self) -> dict[str, float]:
        if not self.is_fitted or self.coefficients is None:
            raise ValueError("Model not fitted")
        return {
            "const": float(self.coefficients[0]),
            "beta_d": float(self.coefficients[1]),
            "beta_w": float(self.coefficients[2]),
            "beta_m": float(self.coefficients[3]),
        }
