from datetime import datetime

from pydantic import BaseModel


class PolicyProposal(BaseModel):
    proposal_id: str
    experiment_id: str
    target_expert: str
    proposed_policy: dict
    rationale: str
    evidence_hash: str
    proposed_by: str
    timestamp: datetime
    status: str  # e.g., "PROPOSED", "REVIEWED", "APPROVED", "REJECTED"
