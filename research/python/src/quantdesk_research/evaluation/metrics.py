from typing import cast

import numpy as np
import polars as pl
from scipy import stats  # type: ignore[import-untyped]
from sklearn.metrics import (  # type: ignore[import-untyped]
    mean_absolute_error,
    mean_squared_error,
    r2_score,
)


def calculate_regression_metrics(y_true: np.ndarray, y_pred: np.ndarray) -> dict[str, float]:
    """Calculate standard regression metrics."""
    return {
        "mse": float(mean_squared_error(y_true, y_pred)),
        "rmse": float(np.sqrt(mean_squared_error(y_true, y_pred))),
        "mae": float(mean_absolute_error(y_true, y_pred)),
        "r2": float(r2_score(y_true, y_pred)),
    }


def calculate_sharpe_ratio(returns: pl.Series, risk_free_rate: float = 0.0) -> float:
    """Calculate annualized Sharpe Ratio."""
    if len(returns) < 2:
        return 0.0
    mean_ret = cast(float, returns.mean()) - risk_free_rate
    std_ret = cast(float, returns.std())
    if std_ret == 0:
        return 0.0
    # Assuming daily returns, annualize by sqrt(252)
    return float((mean_ret / std_ret) * np.sqrt(252))


def calculate_information_coefficient(forecasts: np.ndarray, returns: np.ndarray) -> float:
    """Rank correlation (Spearman) between forecasts and realized returns."""
    if len(forecasts) < 2:
        return 0.0
    correlation, _ = stats.spearmanr(forecasts, returns)
    return float(correlation)


def calculate_max_drawdown(equity_curve: pl.Series) -> float:
    """Calculate maximum drawdown from an equity curve."""
    if len(equity_curve) < 1:
        return 0.0

    # Ensure it's a numpy array for efficient processing
    vals = equity_curve.to_numpy()
    rolling_max = np.maximum.accumulate(vals)
    drawdowns = (vals - rolling_max) / rolling_max
    return float(np.min(drawdowns))
