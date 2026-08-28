import numpy as np
from arch import arch_model  # type: ignore[import-untyped]
from loguru import logger


class GARCHModel:
    """
    GARCH(1,1) model for conditional volatility.
    """

    def __init__(self, p=1, q=1):
        self.p = p
        self.q = q
        self.res = None
        self.is_fitted = False

    def fit(self, returns: np.ndarray):
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

    def export_parameters(self) -> dict:
        if not self.is_fitted or self.res is None:
            raise ValueError("Model not fitted")
        return self.res.params.to_dict()
