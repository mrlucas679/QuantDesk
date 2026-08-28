import numpy as np


def compute_forecast_intervals(
    y_pred: np.ndarray, residuals: np.ndarray, confidence_level: float = 0.95
) -> tuple[np.ndarray, np.ndarray]:
    """
    Computes simple empirical forecast intervals based on historical residuals.
    """
    lower_quantile = (1.0 - confidence_level) / 2.0
    upper_quantile = 1.0 - lower_quantile

    q_low = np.quantile(residuals, lower_quantile)
    q_high = np.quantile(residuals, upper_quantile)

    return y_pred + q_low, y_pred + q_high


def check_empirical_coverage(
    y_true: np.ndarray, lower_bound: np.ndarray, upper_bound: np.ndarray
) -> float:
    """
    Calculates the percentage of true values that fall within the predicted intervals.
    """
    within_bounds = (y_true >= lower_bound) & (y_true <= upper_bound)
    return np.mean(within_bounds)
