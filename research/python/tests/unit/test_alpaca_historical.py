import hashlib
import json
from collections.abc import Mapping
from pathlib import Path
from typing import Any
from urllib.parse import parse_qs, urlparse

import pytest

from quantdesk_research.data.alpaca_historical import (
    credentials_from_environment,
    fetch_historical_bars,
    write_immutable_dataset,
)


def test_fetch_historical_bars_follows_pages_and_records_query_contract() -> None:
    requests: list[tuple[str, Mapping[str, str]]] = []
    pages: list[dict[str, Any]] = [
        {
            "bars": {"SPY": [_bar("2026-01-02T14:30:00Z", 101)]},
            "next_page_token": "next page",
        },
        {
            "bars": {
                "SPY": [
                    _bar("2026-01-01T14:30:00Z", 100),
                    _bar("2026-01-02T14:30:00Z", 101),
                ]
            },
            "next_page_token": None,
        },
    ]

    def fetch_page(url: str, headers: Mapping[str, str]) -> dict[str, Any]:
        requests.append((url, headers))
        return pages[len(requests) - 1]

    bars = fetch_historical_bars(
        "spy",
        "5Min",
        "2026-01-01T00:00:00Z",
        "2026-01-03T00:00:00Z",
        "sip",
        "all",
        "test-key",
        "test-secret",
        fetch_page,
    )

    assert [bar["t"] for bar in bars] == [
        "2026-01-01T14:30:00Z",
        "2026-01-02T14:30:00Z",
    ]
    first_query = parse_qs(urlparse(requests[0][0]).query)
    second_query = parse_qs(urlparse(requests[1][0]).query)
    assert first_query["feed"] == ["sip"]
    assert first_query["adjustment"] == ["all"]
    assert first_query["sort"] == ["asc"]
    assert second_query["page_token"] == ["next page"]
    assert requests[0][1]["APCA-API-KEY-ID"] == "test-key"
    assert requests[0][1]["APCA-API-SECRET-KEY"] == "test-secret"


def test_write_immutable_dataset_hashes_content_and_excludes_credentials(tmp_path: Path) -> None:
    bars = [_bar("2026-01-01T14:30:00Z", 100), _bar("2026-01-02T14:30:00Z", 101)]

    manifest = write_immutable_dataset(tmp_path, "SPY", "1Day", "sip", "all", bars)

    data = (tmp_path / manifest.data_file).read_bytes()
    manifest_text = (tmp_path / "latest-spy-1day-sip.manifest.json").read_text("utf-8")
    assert manifest.sha256 == f"sha256:{hashlib.sha256(data).hexdigest()}"
    assert manifest.row_count == 2
    assert json.loads(data)[0]["t"] == "2026-01-01T14:30:00Z"
    persisted = json.loads(manifest_text)
    assert persisted["dataFile"] == manifest.data_file
    assert persisted["rowCount"] == manifest.row_count
    assert "data_file" not in persisted
    assert "row_count" not in persisted
    assert "test-secret" not in manifest_text
    assert "APCA-API-SECRET-KEY" not in manifest_text


def test_credentials_require_one_complete_bundle() -> None:
    assert credentials_from_environment(
        {"APCA_API_KEY_ID": "key", "APCA_API_SECRET_KEY": "secret"}
    ) == ("key", "secret")
    with pytest.raises(ValueError, match="complete"):
        credentials_from_environment({"APCA_API_KEY_ID": "key"})
    with pytest.raises(ValueError, match="complete"):
        credentials_from_environment(
            {"APCA_API_KEY_ID": "key", "ALPACA_SECRET_KEY": "mixed-secret"}
        )


def test_conflicting_duplicate_bar_fails_closed() -> None:
    pages: list[dict[str, Any]] = [
        {"bars": {"SPY": [_bar("2026-01-01T14:30:00Z", 100)]}, "next_page_token": "next"},
        {"bars": {"SPY": [_bar("2026-01-01T14:30:00Z", 101)]}, "next_page_token": None},
    ]
    call_count = 0

    def fetch_page(_url: str, _headers: Mapping[str, str]) -> dict[str, Any]:
        nonlocal call_count
        result = pages[call_count]
        call_count += 1
        return result

    with pytest.raises(ValueError, match="Conflicting"):
        fetch_historical_bars(
            "SPY", "1Day", "2026-01-01", "2026-01-03", "sip", "all",
            "key", "secret", fetch_page
        )


def _bar(timestamp: str, close: float) -> dict[str, object]:
    return {
        "t": timestamp,
        "o": close - 0.5,
        "h": close + 1,
        "l": close - 1,
        "c": close,
        "v": 1000,
        "n": 100,
        "vw": close,
    }
