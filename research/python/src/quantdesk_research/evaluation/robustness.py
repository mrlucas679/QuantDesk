from collections.abc import Callable, Mapping
from typing import Any

from loguru import logger


class RobustnessTester:
    """
    Sensitivity tests over hyperparameters, window sizes, and cost assumptions.
    """

    def __init__(
        self,
        base_model: Any,
        base_dataset: Any,
        eval_func: Callable[..., Mapping[str, float]],
    ) -> None:
        self.base_model = base_model
        self.base_dataset = base_dataset
        self.eval_func = eval_func

    def test_fee_sensitivity(self, fee_range: list[float]) -> dict[float, float]:
        results: dict[float, float] = {}
        for fee in fee_range:
            metrics = self.eval_func(self.base_model, self.base_dataset, fee=fee)
            results[fee] = metrics.get("sharpe", 0.0)
        return results

    def test_window_sensitivity(self, windows: list[int]) -> dict[int, float]:
        results: dict[int, float] = {}
        for window in windows:
            metrics = self.eval_func(self.base_model, self.base_dataset, window=window)
            results[window] = metrics.get("sharpe", 0.0)
        return results


class TransferTester:
    """
    Tests model performance on different instruments or time periods.
    """

    def __init__(
        self,
        base_model: Any,
        eval_func: Callable[..., Mapping[str, float]],
        data_loader: Callable[..., Any],
    ) -> None:
        self.base_model = base_model
        self.eval_func = eval_func
        self.data_loader = data_loader

    def test_instrument_transfer(self, target_instrument: str) -> float:
        logger.info(f"Testing transfer to {target_instrument}")
        dataset = self.data_loader(target_instrument)
        metrics = self.eval_func(self.base_model, dataset)
        return float(metrics.get("sharpe", 0.0))

    def test_regime_transfer(self, target_regime: str) -> float:
        logger.info(f"Testing transfer to {target_regime}")
        dataset = self.data_loader(regime=target_regime)
        metrics = self.eval_func(self.base_model, dataset)
        return float(metrics.get("sharpe", 0.0))
