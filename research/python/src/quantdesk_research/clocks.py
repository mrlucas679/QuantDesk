from datetime import UTC, datetime
from typing import NewType

Timestamp = NewType("Timestamp", datetime)


def now_utc() -> Timestamp:
    return Timestamp(datetime.now(UTC))


def from_iso(s: str) -> Timestamp:
    return Timestamp(datetime.fromisoformat(s))


def to_iso(ts: datetime) -> str:
    return ts.isoformat()
