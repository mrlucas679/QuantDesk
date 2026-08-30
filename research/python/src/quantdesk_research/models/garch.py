from typing import Any

import numpy as np
from arch import arch_model
from loguru import logger
from numpy.typing import NDArray


class GARCHModel:
    """
    GARCH(1,1) model for conditional volatility.
    """

    def __init__(self, p: int = 1, q: int = 1) -> None:
        self.p = p
        self.q = q
        self.res: Any | None = None
        self.is_fitted = False

    def fit(self, returns: NDArray[np.float64]) -> None:
        # resacle returns to help convergence if necessary, arch_model does it often
        am = arch_model(returns, vol="GARCH", p=self.p, q=self.q, dist="normal")
        try:
            self.res = am.fit(disp="off")
            if self.res.convergence_flag != 0:
                logger.warning("GARCH fit did not converge perfectly")
            self.is_fitted = True
        except Exception as e:
            logger.error(f"GARCH fit failed: {e}")
            self.is_fitted = False
            raise

    def predict_next_vol(self) -> float:
        if not self.is_fitted or self.res is None:
            raise ValueError("Model not fitted")
        forecasts = self.res.forecast(horizon=1)
        # return conditional standard deviation
        return float(np.sqrt(forecasts.variance.values[-1, 0]))

    def export_parameters(self) -> dict[str, Any]:
        if not self.is_fitted or self.res is None:
            raise ValueError("Model not fitted")
        return dict(self.res.params.to_dict())
