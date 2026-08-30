from pathlib import Path

import pytest

from quantdesk_research.data.quote_snapshots import load_quote_snapshots


def _write_snapshots(root: Path, rows: list[str]) -> None:
    directory = root / "quote-snapshots"
    directory.mkdir()
    (directory / "btcusd-20260830.jsonl").write_text("\n".join(rows), encoding="utf-8")


def test_quote_snapshots_require_sufficient_chronological_history(tmp_path: Path) -> None:
    _write_snapshots(
        tmp_path,
        [
            '{"capturedAt":"2026-08-30T09:00:00+00:00","symbol":"BTC/USD","bid":100,"ask":101,"midpoint":100.5,"spreadBps":99.5}',
            '{"capturedAt":"2026-08-30T09:01:00+00:00","symbol":"BTC/USD","bid":101,"ask":102,"midpoint":101.5,"spreadBps":98.5}',
        ],
    )

    snapshots = load_quote_snapshots(tmp_path, "BTC/USD", minimum_records=2)

    assert [snapshot.bid for snapshot in snapshots] == [100, 101]


def test_quote_snapshots_reject_insufficient_or_malformed_evidence(tmp_path: Path) -> None:
    _write_snapshots(tmp_path, ['{"capturedAt":"2026-08-30T09:00:00Z","symbol":"BTC/USD"}'])

    with pytest.raises(ValueError, match="Malformed"):
        load_quote_snapshots(tmp_path, "BTC/USD", minimum_records=1)


def test_quote_snapshots_can_require_depth_bearing_evidence(tmp_path: Path) -> None:
    _write_snapshots(
        tmp_path,
        [
            '{"capturedAt":"2026-08-30T09:00:00+00:00","symbol":"BTC/USD","bid":100,"ask":101,"bidSize":2,"askSize":3,"midpoint":100.5,"spreadBps":99.5}',
            '{"capturedAt":"2026-08-30T09:01:00+00:00","symbol":"BTC/USD","bid":101,"ask":102,"bidSize":0,"askSize":0,"midpoint":101.5,"spreadBps":98.5}',
        ],
    )

    snapshots = load_quote_snapshots(
        tmp_path, "BTC/USD", minimum_records=2, minimum_depth_records=1
    )

    assert snapshots[0].bid_size == 2
    with pytest.raises(ValueError, match="depth history"):
        load_quote_snapshots(tmp_path, "BTC/USD", minimum_records=2, minimum_depth_records=2)
