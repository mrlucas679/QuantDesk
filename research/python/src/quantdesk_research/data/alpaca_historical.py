from __future__ import annotations

import argparse
import hashlib
import json
import os
import sys
from collections.abc import Callable, Mapping
from dataclasses import asdict, dataclass
from datetime import UTC, datetime
from pathlib import Path
from typing import Any, cast
from urllib.error import HTTPError, URLError
from urllib.parse import urlencode
from urllib.request import Request, urlopen

HISTORICAL_BARS_URL = "https://data.alpaca.markets/v2/stocks/bars"
REQUIRED_BAR_FIELDS = frozenset({"t", "o", "h", "l", "c", "v", "n", "vw"})
SUPPORTED_FEEDS = frozenset({"iex", "sip"})
SUPPORTED_ADJUSTMENTS = frozenset({"raw", "split", "dividend", "all"})

JsonObject = dict[str, Any]
PageFetcher = Callable[[str, Mapping[str, str]], JsonObject]


@dataclass(frozen=True)
class HistoricalDatasetManifest:
    """Describes one immutable Alpaca historical-bar dataset."""

    dataset_id: str
    symbol: str
    timeframe: str
    feed: str
    adjustment: str
    start: str
    end: str
    row_count: int
    sha256: str
    generated_at: str
    data_file: str
    source: str = HISTORICAL_BARS_URL


def credentials_from_environment(environment: Mapping[str, str]) -> tuple[str, str]:
    """Return a complete market-data credential bundle without profile fallback."""
    bundles = (
        (environment.get("APCA_API_KEY_ID"), environment.get("APCA_API_SECRET_KEY")),
        (environment.get("ALPACA_API_KEY"), environment.get("ALPACA_SECRET_KEY")),
    )
    for key, secret in bundles:
        if key and secret:
            return key, secret
    raise ValueError("A complete Alpaca market-data credential bundle is required.")


def fetch_historical_bars(
    symbol: str,
    timeframe: str,
    start: str,
    end: str,
    feed: str,
    adjustment: str,
    key_id: str,
    secret_key: str,
    fetch_page: PageFetcher | None = None,
) -> list[JsonObject]:
    """Fetch, validate, order, and deduplicate all pages for one equity symbol."""
    normalized_symbol = _validate_request(symbol, feed, adjustment)
    headers = {
        "APCA-API-KEY-ID": key_id,
        "APCA-API-SECRET-KEY": secret_key,
        "Accept": "application/json",
    }
    page_fetcher = fetch_page or _fetch_json
    page_token: str | None = None
    bars_by_timestamp: dict[str, JsonObject] = {}

    while True:
        url = _build_url(
            normalized_symbol, timeframe, start, end, feed, adjustment, page_token
        )
        payload = page_fetcher(url, headers)
        page_bars = _extract_page_bars(payload, normalized_symbol)
        for bar in page_bars:
            _add_bar(bars_by_timestamp, bar)
        next_token = payload.get("next_page_token")
        page_token = str(next_token) if next_token else None
        if page_token is None:
            break

    bars = [bars_by_timestamp[timestamp] for timestamp in sorted(bars_by_timestamp)]
    if not bars:
        raise ValueError(f"Alpaca returned no {timeframe} bars for {normalized_symbol}.")
    return bars


def write_immutable_dataset(
    output_root: Path,
    symbol: str,
    timeframe: str,
    feed: str,
    adjustment: str,
    bars: list[JsonObject],
) -> HistoricalDatasetManifest:
    """Persist validated bars and a provenance manifest named by the content hash."""
    if not bars:
        raise ValueError("Cannot persist an empty historical dataset.")
    output_root.mkdir(parents=True, exist_ok=True)
    payload = json.dumps(bars, sort_keys=True, separators=(",", ":")).encode("utf-8")
    digest = hashlib.sha256(payload).hexdigest()
    slug = symbol.lower().replace("/", "-")
    dataset_id = f"{slug}-{timeframe.lower()}-{feed}-{digest[:16]}"
    data_file = f"{dataset_id}.json"
    data_path = output_root / data_file
    data_path.write_bytes(payload)

    manifest = HistoricalDatasetManifest(
        dataset_id=dataset_id,
        symbol=symbol,
        timeframe=timeframe,
        feed=feed,
        adjustment=adjustment,
        start=str(bars[0]["t"]),
        end=str(bars[-1]["t"]),
        row_count=len(bars),
        sha256=f"sha256:{digest}",
        generated_at=datetime.now(UTC).isoformat(),
        data_file=data_file,
    )
    manifest_path = output_root / f"latest-{slug}-{timeframe.lower()}-{feed}.manifest.json"
    # This is the same durable manifest contract emitted by the C# publishers.  Keep the
    # wire keys explicit: dataclass snake_case is an implementation detail, not a second schema.
    manifest_path.write_text(
        json.dumps(
            {
                "datasetId": manifest.dataset_id,
                "symbol": manifest.symbol,
                "timeframe": manifest.timeframe,
                "start": manifest.start,
                "end": manifest.end,
                "rowCount": manifest.row_count,
                "sha256": manifest.sha256,
                "generatedAt": manifest.generated_at,
                "dataFile": manifest.data_file,
                "feed": manifest.feed,
                "adjustment": manifest.adjustment,
            },
            indent=2,
            sort_keys=True,
        )
        + "\n",
        encoding="utf-8",
    )
    return manifest


def _validate_request(symbol: str, feed: str, adjustment: str) -> str:
    normalized = symbol.strip().upper()
    if not normalized or len(normalized) > 24 or not normalized.replace(".", "").isalnum():
        raise ValueError("Symbol must be a supported US equity identifier.")
    if feed not in SUPPORTED_FEEDS:
        raise ValueError(f"Unsupported Alpaca stock feed: {feed}.")
    if adjustment not in SUPPORTED_ADJUSTMENTS:
        raise ValueError(f"Unsupported Alpaca adjustment mode: {adjustment}.")
    return normalized


def _build_url(
    symbol: str,
    timeframe: str,
    start: str,
    end: str,
    feed: str,
    adjustment: str,
    page_token: str | None,
) -> str:
    query = {
        "symbols": symbol,
        "timeframe": timeframe,
        "start": start,
        "end": end,
        "feed": feed,
        "adjustment": adjustment,
        "limit": "10000",
        "sort": "asc",
    }
    if page_token:
        query["page_token"] = page_token
    return f"{HISTORICAL_BARS_URL}?{urlencode(query)}"


def _fetch_json(url: str, headers: Mapping[str, str]) -> JsonObject:
    request = Request(url, headers=dict(headers))
    try:
        with urlopen(request, timeout=30) as response:
            payload = json.load(response)
    except HTTPError as error:
        raise RuntimeError(
            f"Alpaca historical data request failed with HTTP {error.code}."
        ) from None
    except URLError:
        raise RuntimeError("Alpaca historical data endpoint was unavailable.") from None
    if not isinstance(payload, dict):
        raise TypeError("Alpaca historical data response must be a JSON object.")
    return cast(JsonObject, payload)


def _extract_page_bars(payload: JsonObject, symbol: str) -> list[JsonObject]:
    bars_object = payload.get("bars")
    if not isinstance(bars_object, dict):
        raise TypeError("Alpaca historical response omitted the bars object.")
    rows = bars_object.get(symbol, [])
    if not isinstance(rows, list):
        raise TypeError("Alpaca historical response contained invalid symbol bars.")
    return cast(list[JsonObject], rows)


def _add_bar(bars_by_timestamp: dict[str, JsonObject], bar: JsonObject) -> None:
    if not isinstance(bar, dict) or not REQUIRED_BAR_FIELDS.issubset(bar):
        raise ValueError("Alpaca historical response contained a malformed bar.")
    timestamp = str(bar["t"])
    previous = bars_by_timestamp.get(timestamp)
    if previous is not None and previous != bar:
        raise ValueError(f"Conflicting historical bars share timestamp {timestamp}.")
    bars_by_timestamp[timestamp] = bar


def main() -> int:
    parser = argparse.ArgumentParser(description="Publish immutable Alpaca equity research bars.")
    parser.add_argument("--symbols", required=True, help="Comma-separated US equity symbols.")
    parser.add_argument("--timeframe", required=True)
    parser.add_argument("--start", required=True)
    parser.add_argument("--end", required=True)
    parser.add_argument("--feed", choices=sorted(SUPPORTED_FEEDS), default="sip")
    parser.add_argument(
        "--adjustment", choices=sorted(SUPPORTED_ADJUSTMENTS), default="all"
    )
    parser.add_argument("--output", type=Path, required=True)
    arguments = parser.parse_args()
    key_id, secret_key = credentials_from_environment(os.environ)

    manifests: list[dict[str, Any]] = []
    for symbol in arguments.symbols.split(","):
        bars = fetch_historical_bars(
            symbol,
            arguments.timeframe,
            arguments.start,
            arguments.end,
            arguments.feed,
            arguments.adjustment,
            key_id,
            secret_key,
        )
        manifest = write_immutable_dataset(
            arguments.output,
            symbol.strip().upper(),
            arguments.timeframe,
            arguments.feed,
            arguments.adjustment,
            bars,
        )
        manifests.append(asdict(manifest))
    print(json.dumps({"datasets": manifests}, sort_keys=True))
    return 0


if __name__ == "__main__":
    sys.exit(main())
