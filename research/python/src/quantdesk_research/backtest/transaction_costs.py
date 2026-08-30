from abc import ABC, abstractmethod
from typing import Any


class TransactionCostModel(ABC):
    @abstractmethod
    def calculate_cost(self, amount: float, price: float, **kwargs: Any) -> float:
        """Return the modeled transaction cost."""


class EquityCostModel(TransactionCostModel):
    def __init__(self, fee_bps: float = 1.0, slippage_bps: float = 1.0) -> None:
        self.fee_bps = fee_bps
        self.slippage_bps = slippage_bps

    def calculate_cost(self, amount: float, price: float, **kwargs: Any) -> float:
        notional = abs(amount * price)
        fees = notional * (self.fee_bps / 10000.0)
        slippage = notional * (self.slippage_bps / 10000.0)
        return fees + slippage


class OptionsCostModel(TransactionCostModel):
    def __init__(self, fee_per_contract: float = 0.65, slippage_bps: float = 5.0) -> None:
        self.fee_per_contract = fee_per_contract
        self.slippage_bps = slippage_bps

    def calculate_cost(self, amount: float, price: float, **kwargs: Any) -> float:
        fees = abs(amount) * self.fee_per_contract
        notional = abs(amount * 100 * price)
        slippage = notional * (self.slippage_bps / 10000.0)
        return fees + slippage


class CryptoCostModel(TransactionCostModel):
    def __init__(self, maker_fee_bps: float = 1.0, taker_fee_bps: float = 2.0) -> None:
        self.maker_fee_bps = maker_fee_bps
        self.taker_fee_bps = taker_fee_bps

    def calculate_cost(
        self, amount: float, price: float, is_maker: bool = False, **kwargs: Any
    ) -> float:
        notional = abs(amount * price)
        fee_bps = self.maker_fee_bps if is_maker else self.taker_fee_bps
        return notional * (fee_bps / 10000.0)
