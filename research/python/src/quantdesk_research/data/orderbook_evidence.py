"""Strict reader for raw C#-captured order-book evidence.

This source is intentionally separate from coalesced latest quotes. Any recorded capture
gap invalidates a microstructure study rather than being imputed or silently skipped.
"""

import json
from dataclasses import dataclass
from datetime import datetime
from itertools import pairwise
from pathlib import Path


@dataclass(frozen=True)
class OrderBookEvidence:
    """One raw aggregate order-book update and its local availability timestamp."""

    captured_at: datetime
    symbol: str
    event_unix_nanoseconds: int
    source_sequence: int
    best_bid: float
    best_ask: float
    bid_depth: float
    ask_depth: float


def load_orderbook_evidence(
    data_root: Path, symbol: str, minimum_records: int = 100_000
) -> list[OrderBookEvidence]:
    """Load gap-free raw order-book evidence suitable for future point-in-time research."""
    if minimum_records < 1:
        raise ValueError("minimum_records must be positive.")
    _require_no_capture_gaps(data_root)
    safe_symbol = "".join(character for character in symbol if character.isalnum()).lower()
    directory = data_root / "orderbook-events"
    records = [
        _parse_orderbook_record(line, symbol)
        for path in sorted(directory.glob(f"{safe_symbol}-*.jsonl"))
        for line in path.read_text(encoding="utf-8").splitlines()
        if line.strip()
    ]
    records.sort(key=lambda record: record.captured_at)
    if len(records) < minimum_records:
        raise ValueError(
            f"Insufficient raw order-book history for {symbol}: "
            f"{len(records)} records, need {minimum_records}."
        )
    if any(current.captured_at <= previous.captured_at for previous, current in pairwise(records)):
        raise ValueError("Order-book evidence timestamps must be strictly increasing.")
    return records


def _require_no_capture_gaps(data_root: Path) -> None:
    """Reject a study when the capture runtime declared evidence discontinuity."""
    directory = data_root / "microstructure-gaps"
    gap_files = sorted(directory.glob("capture-gaps-*.jsonl"))
    for path in gap_files:
        for line in path.read_text(encoding="utf-8").splitlines():
            if not line.strip():
                continue
            try:
                payload = json.loads(line)
                reason = str(payload["reasonCode"])
                gap_count = int(payload["gapCount"])
            except (KeyError, TypeError, ValueError, json.JSONDecodeError) as error:
                raise ValueError("Malformed microstructure capture-gap record.") from error
            if gap_count > 0:
                raise ValueError(f"Microstructure evidence contains capture gap: {reason}.")


def _parse_orderbook_record(line: str, expected_symbol: str) -> OrderBookEvidence:
    """Parse one immutable C# order-book record without repairing invalid evidence."""
    try:
        payload = json.loads(line)
        captured_at = datetime.fromisoformat(str(payload["capturedAt"]))
        symbol = str(payload["symbol"])
        event_unix_nanoseconds = int(payload["eventUnixNanoseconds"])
        source_sequence = int(payload["sourceSequence"])
        best_bid = float(payload["bestBid"])
        best_ask = float(payload["bestAsk"])
        bid_depth = float(payload["bidDepth"])
        ask_depth = float(payload["askDepth"])
    except (KeyError, TypeError, ValueError, json.JSONDecodeError) as error:
        raise ValueError("Malformed raw order-book evidence.") from error
    if (
        symbol != expected_symbol
        or captured_at.tzinfo is None
        or event_unix_nanoseconds <= 0
        or source_sequence < 0
        or best_bid <= 0
        or best_ask < best_bid
        or bid_depth < 0
        or ask_depth < 0
    ):
        raise ValueError("Order-book evidence contains invalid market values.")
    return OrderBookEvidence(
        captured_at,
        symbol,
        event_unix_nanoseconds,
        source_sequence,
        best_bid,
        best_ask,
        bid_depth,
        ask_depth,
    )
