from datetime import datetime

from pydantic import BaseModel


class Forecast(BaseModel):
    expert_id: str
    model_id: str
    model_version: str
    instrument: str
    as_of_time: datetime
    forecast_family: str
    horizon_minutes: int
    point_forecast: float
    prediction_interval: dict | None = None
    confidence: float
    calibration_status: str
    support_domain_status: str
    feature_schema_hash: str
    artifact_hash: str
    status: str
    reason_code: str | None = None
