from datetime import datetime
from typing import Any

from pydantic import BaseModel


class ForecastUncertainty(BaseModel):
    """What a forecast says about its own reliability, and about the family behind it.

    Three separate questions kept separate on purpose, because one number was being asked to answer
    all of them. ``standard_error_bps`` says how wrong today's reading could be.
    ``historical_net_edge_bps`` says what this family actually earned after costs across the sample
    it was validated on. A point forecast answers neither, and a large reading from a family that
    has never made money is not an edge.

    The consuming gate refuses a forecast that omits this rather than reading the silence as
    certainty.

    ``assumed_round_trip_cost_bps`` is what keeps the arithmetic right across the language boundary.
    ``point_forecast`` here is already net of the cost this plane assumed, so an execution plane
    that subtracts cost again charges it twice and rejects every trade. Stating the assumption lets
    execution add it back and substitute what a round trip actually cost -- which it is the only
    side able to measure.
    """

    standard_error_bps: float
    historical_net_edge_bps: float
    historical_net_edge_standard_error_bps: float
    historical_observations: int
    assumed_round_trip_cost_bps: float


class Forecast(BaseModel):
    expert_id: str
    model_id: str
    model_version: str
    instrument: str
    as_of_time: datetime
    forecast_family: str
    horizon_minutes: int
    point_forecast: float
    prediction_interval: dict[str, Any] | None = None
    uncertainty: ForecastUncertainty | None = None
    confidence: float
    calibration_status: str
    support_domain_status: str
    feature_schema_hash: str
    artifact_hash: str
    status: str
    reason_code: str | None = None
