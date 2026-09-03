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
    """One expert's answer, in whichever family it belongs to.

    ``point_forecast`` carries a return in basis points for a directional forecast and a variance
    for a conditional-variance one. ``distribution`` carries the posterior for a regime forecast,
    where a single number cannot say what the model believes -- and where the state *names* matter
    as much as the probabilities, because a retrain that merely renumbers the latent states would
    otherwise make every regime-change interrupt fire on a change that did not happen.

    ``units`` says what the number is in. A variance of 0.0004 is ordinary in percent returns and
    enormous in decimals, and nothing about the figure distinguishes them.
    """

    expert_id: str
    model_id: str
    model_version: str
    instrument: str
    as_of_time: datetime
    forecast_family: str
    horizon_minutes: int
    point_forecast: float
    distribution: dict[str, float] | None = None
    units: str | None = None
    prediction_interval: dict[str, Any] | None = None
    uncertainty: ForecastUncertainty | None = None
    confidence: float
    calibration_status: str
    support_domain_status: str
    feature_schema_hash: str
    artifact_hash: str
    status: str
    reason_code: str | None = None
