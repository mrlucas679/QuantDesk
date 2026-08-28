from datetime import datetime

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

    trades: list[dict]
    metrics: dict
    config_hash: str
    timestamp: datetime
