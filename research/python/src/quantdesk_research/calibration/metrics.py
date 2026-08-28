import numpy as np


def qlike(realized_var, forecast_var, eps=1e-12):
    realized_var = np.maximum(np.asarray(realized_var), eps)
    forecast_var = np.maximum(np.asarray(forecast_var), eps)

    ratio = realized_var / forecast_var
    return np.mean(ratio - np.log(ratio) - 1.0)
