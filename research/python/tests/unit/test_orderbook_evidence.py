from pathlib import Path

import pytest

from quantdesk_research.data.orderbook_evidence import load_orderbook_evidence


def _write_orderbooks(root: Path, rows: list[str]) -> None:
    directory = root / "orderbook-events"
    directory.mkdir()
    (directory / "btcusd-20260830.jsonl").write_text("\n".join(rows), encoding="utf-8")


def test_orderbook_evidence_requires_gap_free_chronological_history(tmp_path: Path) -> None:
    _write_orderbooks(
        tmp_path,
        [
            '{"capturedAt":"2026-08-30T09:00:00+00:00","symbol":"BTC/USD","eventUnixNanoseconds":1,"sourceSequence":0,"bestBid":100,"bestAsk":101,"bidDepth":2,"askDepth":3}',
            '{"capturedAt":"2026-08-30T09:00:01+00:00","symbol":"BTC/USD","eventUnixNanoseconds":2,"sourceSequence":0,"bestBid":101,"bestAsk":102,"bidDepth":4,"askDepth":5}',
        ],
    )

    records = load_orderbook_evidence(tmp_path, "BTC/USD", minimum_records=2)

    assert [record.best_bid for record in records] == [100, 101]


def test_orderbook_evidence_rejects_capture_gap(tmp_path: Path) -> None:
    _write_orderbooks(
        tmp_path,
        ['{"capturedAt":"2026-08-30T09:00:00+00:00","symbol":"BTC/USD","eventUnixNanoseconds":1,"sourceSequence":0,"bestBid":100,"bestAsk":101,"bidDepth":2,"askDepth":3}'],
    )
    gap_directory = tmp_path / "microstructure-gaps"
    gap_directory.mkdir()
    (gap_directory / "capture-gaps-20260830.jsonl").write_text(
        '{"gapCount":1,"reasonCode":"stream_disconnected"}', encoding="utf-8"
    )

    with pytest.raises(ValueError, match="capture gap"):
        load_orderbook_evidence(tmp_path, "BTC/USD", minimum_records=1)


def test_orderbook_evidence_rejects_invalid_market_values(tmp_path: Path) -> None:
    _write_orderbooks(
        tmp_path,
        ['{"capturedAt":"2026-08-30T09:00:00+00:00","symbol":"BTC/USD","eventUnixNanoseconds":1,"sourceSequence":0,"bestBid":102,"bestAsk":101,"bidDepth":2,"askDepth":3}'],
    )

    with pytest.raises(ValueError, match="invalid market values"):
        load_orderbook_evidence(tmp_path, "BTC/USD", minimum_records=1)
