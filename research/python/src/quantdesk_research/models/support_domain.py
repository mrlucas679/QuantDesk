from typing import Any

from pydantic import BaseModel


class SupportDomain(BaseModel):
    """
    Defines where a model's evidence applies.
    """

    asset_classes: list[str]
    instruments: Any = None
    min_liquidity_rank: Any = None
    market_sessions: list[str] = ["RTH"]
    data_granularity: str = "1m"
    min_history_required: int
    max_forecast_horizon: int

    def is_applicable(
        self, instrument: str, asset_class: str, session: str, history_len: int
    ) -> bool:
        if asset_class not in self.asset_classes:
            return False
        if self.instruments is not None and instrument not in self.instruments:
            return False
        if session not in self.market_sessions:
            return False
        return not history_len < self.min_history_required
