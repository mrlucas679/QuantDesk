from datetime import datetime
from typing import Any

from pydantic import BaseModel


class Episode(BaseModel):
    episode_id: str
    experiment_id: str
    instrument: str
    start_time: datetime
    end_time: datetime

    initial_cash: float
    final_cash: float
    total_pnl: float
    returns: float

    trades: list[dict[str, Any]]
    metrics: dict[str, Any]
    config_hash: str
    timestamp: datetime
