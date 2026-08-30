import numpy as np
from numpy.typing import NDArray


def qlike(
    realized_var: NDArray[np.float64], forecast_var: NDArray[np.float64], eps: float = 1e-12
) -> float:
    realized_var = np.maximum(np.asarray(realized_var), eps)
    forecast_var = np.maximum(np.asarray(forecast_var), eps)

    ratio = realized_var / forecast_var
    return float(np.mean(ratio - np.log(ratio) - 1.0))
