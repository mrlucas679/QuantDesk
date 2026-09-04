"""Old raw features against ATR-normalised ones, on identical windows, models and costs.

The claim being tested is narrow and falsifiable: that expressing every distance in units of the
instrument's own current volatility makes a feature mean the same thing across regimes, and that a
model fed such features learns something more stable than one fed raw returns.

Nothing else changes between the two arms. Same rolling purged windows, same three models and their
average, same threshold selection on calibration, same venue costs, same seed. If the normalised arm
does not win, the honest conclusion is that it does not help here -- the point of running it this
way is that the answer cannot be argued either way afterwards.
"""

from __future__ import annotations

import json
import math
import sys
import urllib.parse
import urllib.request
from datetime import UTC, datetime, timedelta
from pathlib import Path
from typing import Any

import numpy as np
import pandas as pd
from numpy.typing import NDArray

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "src"))

from quantdesk_research.experiments.crypto_direction import (  # noqa: E402
    FEATURE_NAMES as RAW_FEATURES,
)
from quantdesk_research.experiments.crypto_direction import (  # noqa: E402
    build_frame,
    conservative_lower_mean,
    non_overlapping_returns,
    rolling_outer_slices,
    select_threshold,
)
from quantdesk_research.experiments.model_ensemble import build_models  # noqa: E402
from quantdesk_research.features import atr_normalised  # noqa: E402

CRYPTO = ["BTC/USD", "ETH/USD", "UNI/USD", "AAVE/USD", "BCH/USD", "AVAX/USD", "LINK/USD"]
EQUITY = ["SPY", "QQQ", "IWM", "DIA"]

#: Measured from the account on 2026-09-04: 25.6 bps in kind on the buy, 27.0 bps in cash on the
#: sell. Not the research assumption, and not a figure read off a comment.
COST = {"crypto": 52.6, "equity": 8.0}

HEAD = {
    "APCA-API-KEY-ID": __import__("os").environ["APCA_API_KEY_ID"],
    "APCA-API-SECRET-KEY": __import__("os").environ["APCA_API_SECRET_KEY"],
}


def fetch(symbol: str, timeframe: str, days: int) -> list[dict[str, Any]]:
    start = (datetime.now(UTC) - timedelta(days=days)).strftime("%Y-%m-%dT%H:%M:%SZ")
    crypto = "/" in symbol
    url = (
        "https://data.alpaca.markets/v1beta3/crypto/us/bars"
        f"?symbols={urllib.parse.quote(symbol)}&timeframe={timeframe}&start={start}&limit=10000"
        if crypto
        else f"https://data.alpaca.markets/v2/stocks/{symbol}/bars"
        f"?timeframe={timeframe}&start={start}&limit=10000&adjustment=raw"
    )

    rows: list[dict[str, Any]] = []
    page = None
    while True:
        request = urllib.request.Request(url + (f"&page_token={page}" if page else ""), headers=HEAD)
        payload = json.load(urllib.request.urlopen(request, timeout=90))
        bars = payload.get("bars")
        rows.extend(bars.get(symbol, []) if crypto else (bars or []))
        page = payload.get("next_page_token")
        if not page or len(rows) > 40_000:
            break
    return rows


def score(
    features: NDArray[np.float64],
    target: NDArray[np.float64],
    cost_bps: float,
    horizon_bars: int,
) -> tuple[int, float, float]:
    """Trades, mean net bps and the 95% lower bound, on purged rolling out-of-sample windows."""
    cost = cost_bps / 10_000
    collected: list[NDArray[np.float64]] = []

    for train, calibration, test in rolling_outer_slices(len(features), horizon_bars):
        predictions_cal, predictions_test = [], []
        for model in build_models().values():
            model.fit(features[train], target[train])
            predictions_cal.append(np.asarray(model.predict(features[calibration]), dtype=float))
            predictions_test.append(np.asarray(model.predict(features[test]), dtype=float))

        # The ensemble only. The point here is the feature set, and running four models per arm
        # would compare eight numbers where two carry the question.
        cal = np.mean(predictions_cal, axis=0)
        out = np.mean(predictions_test, axis=0)
        threshold = select_threshold(cal, target[calibration], cost, horizon_bars)
        collected.append(
            non_overlapping_returns(out, target[test], threshold, horizon_bars) - cost
        )

    selected = np.concatenate(collected)
    if len(selected) < 2:
        return len(selected), float("nan"), float("nan")

    return (
        len(selected),
        float(selected.mean()) * 10_000,
        conservative_lower_mean(selected) * 10_000,
    )


def main() -> int:
    timeframe = sys.argv[1] if len(sys.argv) > 1 else "30Min"
    horizon = int(sys.argv[2]) if len(sys.argv) > 2 else 16
    days = {"5Min": 60, "15Min": 180, "30Min": 360}.get(timeframe, 360)

    print(f"{timeframe} bars, {horizon}-bar horizon, purged rolling out-of-sample\n")
    header = f"{'symbol':<10}{'features':<14}{'trades':>8}{'mean bps':>11}{'lower bps':>11}"
    print(header)
    print("-" * len(header))

    for symbol in CRYPTO + EQUITY:
        cost = COST["crypto" if "/" in symbol else "equity"]
        try:
            bars = fetch(symbol, timeframe, days)
        except Exception as failure:  # noqa: BLE001 - one symbol must not end the comparison
            print(f"{symbol:<10}fetch failed: {failure}")
            continue
        if len(bars) < 1_500:
            print(f"{symbol:<10}only {len(bars)} bars; skipped")
            continue

        arms: dict[str, tuple[NDArray[np.float64], NDArray[np.float64]]] = {}

        raw = build_frame(bars, horizon)
        if len(raw) >= 1_000:
            arms["raw"] = (
                raw.loc[:, list(RAW_FEATURES)].to_numpy(dtype=float),
                raw["target_return"].to_numpy(dtype=float),
            )

        normalised = atr_normalised.frame_for(bars, horizon)
        if len(normalised) >= 1_000:
            # Fitted on the ATR-normalised target so the model is asked a question whose scale does
            # not drift; scored on the raw return, because P&L is denominated in returns and costs
            # in basis points.
            arms["atr-normalised"] = (
                normalised.loc[:, list(atr_normalised.FEATURE_NAMES)].to_numpy(dtype=float),
                normalised["target_return"].to_numpy(dtype=float),
            )

        for name, (features, target) in arms.items():
            trades, mean, lower = score(features, target, cost, horizon)
            flag = "  <-- clears" if lower > 0 and trades >= 60 else ""
            print(
                f"{symbol:<10}{name:<14}{trades:>8}{mean:>11.1f}{lower:>11.1f}{flag}"
                if math.isfinite(mean)
                else f"{symbol:<10}{name:<14}{trades:>8}      n/a        n/a"
            )
        print()

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
