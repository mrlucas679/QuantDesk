from abc import ABC, abstractmethod


class FillModel(ABC):
    @abstractmethod
    def get_fill_price(self, bid: float, ask: float, side: str) -> float:
        """Return the simulated execution price for an order."""

    def get_fill_quantity(self, requested_qty: float, **kwargs) -> float:
        return requested_qty


class MidpointFillModel(FillModel):
    def get_fill_price(self, bid: float, ask: float, side: str) -> float:
        return (bid + ask) / 2.0


class SpreadFillModel(FillModel):
    def __init__(self, capture_pct: float = 0.0):
        self.capture_pct = capture_pct

    def get_fill_price(self, bid: float, ask: float, side: str) -> float:
        mid = (bid + ask) / 2.0
        half_spread = (ask - bid) / 2.0

        if side == "buy":
            return mid + half_spread * (1.0 - self.capture_pct)
        else:
            return mid - half_spread * (1.0 - self.capture_pct)


class AdverseSelectionFillModel(FillModel):
    def get_fill_price(self, bid: float, ask: float, side: str) -> float:
        # Fill at the far side (ask for buy, bid for sell)
        return ask if side == "buy" else bid


class PartialFillModel(FillModel):
    def __init__(self, fill_ratio: float = 0.5):
        self.fill_ratio = fill_ratio

    def get_fill_price(self, bid: float, ask: float, side: str) -> float:
        return ask if side == "buy" else bid

    def get_fill_quantity(self, requested_qty: float, **kwargs) -> float:
        return requested_qty * self.fill_ratio
