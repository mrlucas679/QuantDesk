from datetime import datetime

from pydantic import BaseModel


class ShadowAudit(BaseModel):
    audit_id: str
    start_time: datetime
    end_time: datetime

    reconstructed_portfolio: dict
    reconstructed_risk: dict

    runtime_portfolio: dict
    runtime_risk: dict

    mismatches: list[dict]
    status: str  # "PASS", "FAIL"
    diff_report: str
    timestamp: datetime
