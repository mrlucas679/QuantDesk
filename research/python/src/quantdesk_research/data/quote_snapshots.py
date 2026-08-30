"""Strict reader for C#-captured, point-in-time executable crypto quotes."""

import json
from dataclasses import dataclass
from datetime import datetime
from decimal import Decimal
from itertools import pairwise
from pathlib import Path


@dataclass(frozen=True)
class QuoteSnapshot:
    """One two-sided quote whose timestamp is also its availability time."""

    captured_at: datetime
    symbol: str
    bid: Decimal
    ask: Decimal
    bid_size: Decimal
    ask_size: Decimal
    midpoint: Decimal
    spread_bps: Decimal


def load_quote_snapshots(
    data_root: Path,
    symbol: str,
    minimum_records: int = 10_000,
    minimum_depth_records: int = 0,
) -> list[QuoteSnapshot]:
    """Load immutable quote evidence and require depth-bearing rows when requested."""
    if minimum_records < 1:
        raise ValueError("minimum_records must be positive.")
    if minimum_depth_records < 0:
        raise ValueError("minimum_depth_records cannot be negative.")
    directory = data_root / "quote-snapshots"
    safe_symbol = "".join(character for character in symbol if character.isalnum()).lower()
    records = [
        _parse_snapshot(line, symbol)
        for path in sorted(directory.glob(f"{safe_symbol}-*.jsonl"))
        for line in path.read_text(encoding="utf-8").splitlines()
        if line.strip()
    ]
    records.sort(key=lambda record: record.captured_at)
    if len(records) < minimum_records:
        raise ValueError(
            f"Insufficient point-in-time quote history for {symbol}: "
            f"{len(records)} records, need {minimum_records}."
        )
    if any(current.captured_at <= previous.captured_at for previous, current in pairwise(records)):
        raise ValueError("Quote snapshot timestamps must be strictly increasing.")
    depth_record_count = sum(record.bid_size > 0 and record.ask_size > 0 for record in records)
    if depth_record_count < minimum_depth_records:
        raise ValueError(
            f"Insufficient point-in-time depth history for {symbol}: "
            f"{depth_record_count} records, need {minimum_depth_records}."
        )
    return records


def _parse_snapshot(line: str, expected_symbol: str) -> QuoteSnapshot:
    """Parse and validate an individual C# quote snapshot without repairing its evidence."""
    try:
        payload = json.loads(line)
        captured_at = datetime.fromisoformat(str(payload["capturedAt"]))
        symbol = str(payload["symbol"])
        bid = Decimal(str(payload["bid"]))
        ask = Decimal(str(payload["ask"]))
        bid_size = Decimal(str(payload.get("bidSize", 0)))
        ask_size = Decimal(str(payload.get("askSize", 0)))
        midpoint = Decimal(str(payload["midpoint"]))
        spread_bps = Decimal(str(payload["spreadBps"]))
    except (KeyError, TypeError, ValueError, json.JSONDecodeError) as error:
        raise ValueError("Malformed point-in-time quote snapshot.") from error
    if symbol != expected_symbol or captured_at.tzinfo is None:
        raise ValueError("Quote snapshot has an unexpected symbol or no timezone.")
    if bid <= 0 or ask < bid or bid_size < 0 or ask_size < 0 or midpoint <= 0 or spread_bps < 0:
        raise ValueError("Quote snapshot contains an invalid executable spread.")
    return QuoteSnapshot(captured_at, symbol, bid, ask, bid_size, ask_size, midpoint, spread_bps)
