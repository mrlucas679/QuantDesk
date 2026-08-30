from datetime import datetime
from typing import Any

from pydantic import BaseModel


class ShadowAudit(BaseModel):
    audit_id: str
    start_time: datetime
    end_time: datetime

    reconstructed_portfolio: dict[str, Any]
    reconstructed_risk: dict[str, Any]

    runtime_portfolio: dict[str, Any]
    runtime_risk: dict[str, Any]

    mismatches: list[dict[str, Any]]
    status: str  # "PASS", "FAIL"
    diff_report: str
    timestamp: datetime
