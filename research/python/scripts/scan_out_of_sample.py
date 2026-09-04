"""The strategy scan, re-run the way the handbook's section 20.1 requires.

Four things differ from the first run, and all four change what may be concluded.

1. Cost.        68 bps was a guess taken from a contaminated equity measurement. The broker-side
                reconstruction of 2026-09-02 measured 33.7 bps across 75 round trips.
2. Definitions. VWAP is session-scoped for equities, the volume anomaly is scored against the same
                time of day on previous days, and the momentum horizons are spans of time. These
                match the C# indicator set as of ee573a0.
3. Split.       Chronological train / test. The first run ranked and reported on one undivided
                block, which is selection and evaluation on the same data.
4. Correction.  PBO and the Deflated Sharpe ratio, with the trial count stated. The first run
                reported 95% intervals for a single hypothesis after examining ninety.
"""

from __future__ import annotations

import json
import math
import os
import statistics
import urllib.request
from datetime import UTC, datetime, timedelta

import pathlib

import numpy as np

from quantdesk_research.evaluation.deflated_sharpe import calculate_deflated_sharpe_ratio
from quantdesk_research.evaluation.pbo import calculate_pbo

CRYPTO = ["BTC/USD", "ETH/USD", "UNI/USD", "AAVE/USD", "BCH/USD", "AVAX/USD", "LINK/USD"]
EQUITY = ["SPY", "QQQ", "IWM", "DIA"]

# What the venue actually charges for a round trip, in basis points.
#
# This said 33.7 for crypto under a comment claiming it was measured rather than assumed. It was
# the assumption. The account is charged about 60, and the gap is not academic: every crypto figure
# this scan produced was roughly 26 bps too generous, which is the whole reason crypto rules cleared
# a committee floor that honest equity rules did not, and why the registry showed a "best" crypto
# rule at +1.5 bps net that is really about -25.
# 52.6, and this one is measured from the account rather than read off a comment. Alpaca charges
# roughly 25.6 bps in kind on the buy -- the delivered quantity is short by that much -- and about
# 27.0 bps in USD on the sell. Reconstructed over 132 buys and 130 sells: $35.39 of missing
# quantity and $36.25 of cash the fills do not account for.
#
# The two previous values here were both guesses wearing a comment's authority. 33.7 was the
# research assumption; 60.0 was mine, written from a code comment on 2026-09-04 without measuring.
CRYPTO_COST = 52.6
EQUITY_COST = 8.0

# Holding periods in 5-minute bars: one hour through twelve.
#
# It stopped at 48 (four hours), which is exactly where the interesting behaviour starts. Edge grows
# with the square root of holding time while the toll stays fixed, and the 2026-09-04 model
# comparison measured that directly: equity mean net edge is negative at fifteen minutes, crosses
# zero somewhere past an hour, and reaches +17 bps on IWM at four hours and +20 at twelve. A scan
# that never looked past four hours could not have found any of it.
HOLDS = (12, 24, 48, 96, 144)
TRAIN_FRACTION = 0.6

# The bar the scan reads. Everything in this system has been 5-minute bars and nothing else -- one
# timeframe, never chosen, just inherited.
#
# It is worth questioning on its own terms. A rule firing on 5-minute bars gets many more
# opportunities than the same rule on 30-minute bars, and each one is charged the same fixed toll.
# Coarser bars also mean fewer, larger moves per decision, which is the same square-root argument
# the holding-period sweep tests, applied to the sampling interval instead of the hold.
BARS = ("5Min", "15Min", "30Min")

HEAD = {
    "APCA-API-KEY-ID": os.environ["APCA_API_KEY_ID"],
    "APCA-API-SECRET-KEY": os.environ["APCA_API_SECRET_KEY"],
}


# --------------------------------------------------------------------------------- data
def fetch(symbol: str, timeframe: str = "5Min", days: int = 60) -> dict[str, np.ndarray] | None:
    start = (datetime.now(UTC) - timedelta(days=days)).strftime("%Y-%m-%dT%H:%M:%SZ")
    crypto = "/" in symbol
    if crypto:
        url = (
            "https://data.alpaca.markets/v1beta3/crypto/us/bars"
            f"?symbols={urllib.parse.quote(symbol)}"
            f"&timeframe={urllib.parse.quote(timeframe)}&start={start}&limit=10000"
        )
    else:
        url = (
            f"https://data.alpaca.markets/v2/stocks/{symbol}/bars"
            f"?timeframe={urllib.parse.quote(timeframe)}&start={start}"
            "&limit=10000&adjustment=raw"
        )

    rows: list[dict[str, object]] = []
    page = None
    while True:
        request = urllib.request.Request(url + (f"&page_token={page}" if page else ""), headers=HEAD)
        payload = json.load(urllib.request.urlopen(request, timeout=90))
        bars = payload.get("bars")
        chunk = bars.get(symbol, []) if crypto else (bars or [])
        rows.extend(chunk)
        page = payload.get("next_page_token")
        if not page or len(rows) > 40_000:
            break

    if len(rows) < 500:
        return None

    return {
        "t": np.array([np.datetime64(r["t"][:19]) for r in rows]),
        "c": np.array([float(r["c"]) for r in rows]),
        "h": np.array([float(r["h"]) for r in rows]),
        "l": np.array([float(r["l"]) for r in rows]),
        "v": np.array([float(r["v"]) for r in rows]),
    }


# ---------------------------------------------------------------------------- indicators
def ema(x: np.ndarray, n: int) -> np.ndarray:
    out = np.full(len(x), np.nan)
    if len(x) < n:
        return out
    k = 2.0 / (n + 1)
    out[n - 1] = x[:n].mean()
    for i in range(n, len(x)):
        out[i] = x[i] * k + out[i - 1] * (1 - k)
    return out


def rma(x: np.ndarray, n: int) -> np.ndarray:
    """Wilder smoothing: decays at 1/n, not the 2/(n+1) of an EMA."""
    out = np.full(len(x), np.nan)
    finite = np.where(np.isfinite(x))[0]
    if len(finite) < n:
        return out
    s = finite[0] + n - 1
    if s >= len(x):
        return out
    out[s] = np.nanmean(x[finite[0] : s + 1])
    for i in range(s + 1, len(x)):
        out[i] = (out[i - 1] * (n - 1) + x[i]) / n
    return out


def rsi(c: np.ndarray, n: int = 14) -> np.ndarray:
    d = np.diff(c, prepend=c[0])
    gain, loss = rma(np.maximum(d, 0), n), rma(-np.minimum(d, 0), n)
    with np.errstate(divide="ignore", invalid="ignore"):
        return 100 - 100 / (1 + gain / loss)


def true_range(h: np.ndarray, low: np.ndarray, c: np.ndarray) -> np.ndarray:
    pc = np.roll(c, 1)
    pc[0] = c[0]
    return np.maximum(h - low, np.maximum(np.abs(h - pc), np.abs(low - pc)))


def bollinger(c: np.ndarray, n: int = 20, k: float = 2.0):
    mid = np.full(len(c), np.nan)
    sd = np.full(len(c), np.nan)
    for i in range(n - 1, len(c)):
        w = c[i - n + 1 : i + 1]
        mid[i], sd[i] = w.mean(), w.std(ddof=0)
    return mid, mid + k * sd, mid - k * sd


def macd(c: np.ndarray):
    line = ema(c, 12) - ema(c, 26)
    return line - ema(np.nan_to_num(line), 9)


def stochastic(h, low, c, n=14, smooth=3):
    k = np.full(len(c), np.nan)
    for i in range(n - 1, len(c)):
        hi, lo = h[i - n + 1 : i + 1].max(), low[i - n + 1 : i + 1].min()
        k[i] = 100 * (c[i] - lo) / (hi - lo) if hi > lo else 50.0
    d = np.full(len(c), np.nan)
    for i in range(n + smooth - 2, len(c)):
        d[i] = np.nanmean(k[i - smooth + 1 : i + 1])
    return k, d


def adx(h, low, c, n=14):
    up, dn = h - np.roll(h, 1), np.roll(low, 1) - low
    up[0] = dn[0] = 0
    plus = np.where((up > dn) & (up > 0), up, 0.0)
    minus = np.where((dn > up) & (dn > 0), dn, 0.0)
    atr_ = rma(true_range(h, low, c), n)
    with np.errstate(divide="ignore", invalid="ignore"):
        pdi, mdi = 100 * rma(plus, n) / atr_, 100 * rma(minus, n) / atr_
        dx = 100 * np.abs(pdi - mdi) / (pdi + mdi)
    return rma(dx, n), pdi, mdi


def donchian(h, low, n=20):
    hi = np.full(len(h), np.nan)
    for i in range(n - 1, len(h)):
        hi[i] = h[i - n + 1 : i + 1].max()
    return hi


def obv(c: np.ndarray, v: np.ndarray) -> np.ndarray:
    return np.concatenate([[0.0], np.cumsum(np.sign(np.diff(c)) * v[1:])])


def session_starts(t: np.ndarray) -> np.ndarray:
    """Boundaries taken from gaps wider than three bars, matching the C# rule."""
    gaps = np.diff(t).astype("timedelta64[s]").astype(float)
    typical = np.median(gaps) if len(gaps) else BAR_MINUTES * 60
    return np.concatenate([[True], gaps > typical * 3])


def session_vwap(h, low, c, v, t) -> np.ndarray:
    """VWAP accumulated from each session's start. The definition VWAP actually has."""
    out = np.full(len(c), np.nan)
    starts = session_starts(t)
    weighted = volume = 0.0
    for i in range(len(c)):
        if starts[i]:
            weighted = volume = 0.0
        weighted += (h[i] + low[i] + c[i]) / 3.0 * v[i]
        volume += v[i]
        out[i] = weighted / volume if volume > 0 else np.nan
    return out


def rolling_vwap(h, low, c, v, n=48) -> np.ndarray:
    out = np.full(len(c), np.nan)
    typical = (h + low + c) / 3.0
    for i in range(n - 1, len(c)):
        vol = v[i - n + 1 : i + 1].sum()
        out[i] = (typical[i - n + 1 : i + 1] * v[i - n + 1 : i + 1]).sum() / vol if vol > 0 else np.nan
    return out


def time_of_day_volume_z(v: np.ndarray, t: np.ndarray, minimum_prior: int = 5) -> np.ndarray:
    """Volume against the same time of day on previous days, not against its own window."""
    out = np.full(len(v), np.nan)
    gaps = np.diff(t).astype("timedelta64[s]").astype(float)
    bucket_seconds = max(float(np.median(gaps)) if len(gaps) else BAR_MINUTES * 60, 60.0)

    midnight = t.astype("datetime64[D]").astype("datetime64[s]")
    seconds_into_day = (t.astype("datetime64[s]") - midnight).astype(float)
    buckets = (seconds_into_day // bucket_seconds).astype(int)

    prior: dict[int, list[float]] = {}
    for i, bucket in enumerate(buckets):
        history = prior.setdefault(int(bucket), [])
        if len(history) >= minimum_prior:
            mean = statistics.fmean(history)
            sd = statistics.pstdev(history)
            out[i] = (v[i] - mean) / sd if sd > 0 else 0.0
        history.append(float(v[i]))
    return out


def build(bars, session_scoped: bool) -> dict[str, np.ndarray]:
    c, h, low, v, t = bars["c"], bars["h"], bars["l"], bars["v"], bars["t"]
    n = len(c)
    r14 = rsi(c, 14)
    mid, up, lo = bollinger(c, 20)
    hist = macd(c)
    k, d = stochastic(h, low, c)
    adx14, pdi, mdi = adx(h, low, c)
    dhi = donchian(h, low, 20)
    e12, e48 = ema(c, 12), ema(c, 48)
    a14 = rma(true_range(h, low, c), 14)
    vw = session_vwap(h, low, c, v, t) if session_scoped else rolling_vwap(h, low, c, v, 48)
    vol_z = time_of_day_volume_z(v, t)
    ob = obv(c, v)
    obv_slope = np.concatenate([[np.nan] * 12, ob[12:] - ob[:-12]])
    def prev(x: np.ndarray) -> np.ndarray:
        return np.roll(x, 1)

    # Horizons measured in time. Twelve five-minute bars equal an hour only on an unbroken feed.
    def ret_over(minutes: int) -> np.ndarray:
        out = np.full(n, np.nan)
        cutoff = np.timedelta64(minutes * 60, "s")
        j = 0
        for i in range(n):
            while j < i and t[i] - t[j] > cutoff:
                j += 1
            k_ = j - 1 if j > 0 and t[i] - t[j - 1] >= cutoff else -1
            if k_ >= 0:
                out[i] = (c[i] / c[k_] - 1) * 10_000
        return out

    hour, quarter = ret_over(60), ret_over(15)
    width = np.where(mid > 0, (up - lo) / mid, np.nan)
    width_pct = np.full(n, np.nan)
    for i in range(96, n):
        window = width[i - 96 : i]
        finite = window[np.isfinite(window)]
        if finite.size:
            width_pct[i] = (finite < width[i]).mean()

    with np.errstate(invalid="ignore"):
        vwap_gap = np.where(a14 > 0, (c - vw) / a14, np.nan)

    return {
        "momentum-dual-horizon": (hour > 0) & (quarter > 0),
        "ema-cross-12-48": (e12 > e48) & (prev(e12) <= prev(e48)),
        "macd-histogram-flip": (hist > 0) & (prev(hist) <= 0),
        "adx-filtered-trend": (adx14 > 25) & (pdi > mdi) & (c > e48),
        "rsi-oversold-reversal": (r14 > 30) & (prev(r14) <= 30),
        "bollinger-lower-touch": (c > lo) & (prev(c) <= prev(lo)),
        "stochastic-oversold-cross": (k > d) & (prev(k) <= prev(d)) & (k < 30),
        "vwap-reversion": (vwap_gap < -1.5),
        "donchian-breakout-20": (c > dhi) & (prev(c) <= prev(dhi)),
        "bollinger-upper-break": (c > up) & (prev(c) <= prev(up)),
        "volatility-squeeze-break": (width_pct < 0.2) & (c > up),
        "volume-surge-breakout": (c > dhi) & (vol_z > 2.0),
        "obv-confirmed-trend": (obv_slope > 0) & (c > e48) & (r14 > 50),
        "atr-expansion-trend": (a14 > prev(a14)) & (c > e48) & (hour > 0),
        "supertrend-flip": (c > e48) & (prev(c) <= prev(e48)) & (adx14 > 20),
    }


# ---------------------------------------------------------------------------- evaluation
def trades(entries: np.ndarray, c: np.ndarray, hold: int, cost: float) -> list[float]:
    """Non-overlapping: once entered, skip forward by the holding period."""
    nets, i = [], 0
    entries = np.nan_to_num(entries.astype(float), nan=0.0).astype(bool)
    while i < len(c) - hold:
        if entries[i]:
            nets.append((c[i + hold] / c[i] - 1) * 10_000 - cost)
            i += hold
        else:
            i += 1
    return nets


def summarise(nets: list[float]):
    n = len(nets)
    if n < 12:
        return None
    mean = statistics.fmean(nets)
    sd = statistics.stdev(nets)
    lower = mean - 1.96 * sd / math.sqrt(n)
    sharpe = mean / sd * math.sqrt(n) if sd > 0 else 0.0
    return n, mean, lower, sharpe, sd


def run(
    symbols: list[str],
    cost: float,
    label: str,
    session_scoped: bool,
    timeframe: str = "5Min",
    days: int = 60,
) -> None:
    print("")
    print("============================================================================================")
    print(f"{label}   {timeframe} bars   cost {cost:.1f} bps per round trip")
    print("============================================================================================")

    train: dict[str, list[float]] = {}
    test: dict[str, list[float]] = {}
    curves: dict[str, list[float]] = {}

    for symbol in symbols:
        bars = fetch(symbol, timeframe, days)
        if bars is None:
            print(f"  {symbol}: insufficient history")
            continue
        cut = int(len(bars["c"]) * TRAIN_FRACTION)
        strategies = build(bars, session_scoped)
        for name, entries in strategies.items():
            for hold in HOLDS:
                key = f"{name}|{hold}"
                train.setdefault(key, []).extend(trades(entries[:cut], bars["c"][:cut], hold, cost))
                test.setdefault(key, []).extend(trades(entries[cut:], bars["c"][cut:], hold, cost))
                curves.setdefault(key, []).extend(trades(entries, bars["c"], hold, cost))

    rows = []
    for key in sorted(train):
        in_sample = summarise(train[key])
        out_sample = summarise(test[key])
        if in_sample and out_sample:
            rows.append((key, in_sample, out_sample))

    if not rows:
        print("  no family produced enough non-overlapping trades to evaluate")
        return

    rows.sort(key=lambda r: -r[1][1])   # ranked on the training half only

    print(f"{'strategy|hold':32s} {'n_tr':>5s} {'train':>8s} "
          f"{'n_te':>5s} {'test':>8s} {'test l95':>9s} {'held up':>8s}")
    print("-" * 92)
    for key, (n1, m1, _, _, _), (n2, m2, l2, _, _) in rows:
        held = "yes" if (m1 > 0 and m2 > 0) else ""
        print(f"{key:32s} {n1:5d} {m1:8.1f} {n2:5d} {m2:8.1f} {l2:9.1f} {held:>8s}")

    # -------------------------------------------------- selection correction
    trials = len(rows)
    width = min(len(curves[key]) for key, _, _ in rows)
    width = min(width, 400)
    if width >= 40:
        matrix = np.column_stack([np.array(curves[key][-width:]) for key, _, _ in rows])
        pbo = calculate_pbo(matrix, n_partitions=10, random_seed=20260902)
    else:
        pbo = float("nan")

    best_sharpe = max((r[2][3] for r in rows), default=0.0)
    best = max(rows, key=lambda r: r[2][3])
    n_best = best[2][0]
    dsr = calculate_deflated_sharpe_ratio(
        observed_sharpe=best_sharpe,
        n_trials=trials,
        sharpe_variance=statistics.pvariance([r[2][3] for r in rows]) if trials > 1 else 0.0,
        t_samples=n_best,
    )

    print("-" * 92)
    print(f"  trials examined            {trials}")
    print(f"  probability of backtest overfitting (PBO)   {pbo:.3f}")
    print(f"  best out-of-sample Sharpe   {best[0]}  {best_sharpe:.3f}  (n={n_best})")
    print(f"  deflated Sharpe ratio       {dsr:.3f}   "
          f"{'survives' if dsr > 0.95 else 'does not survive the trial count'}")

    # Relative to this script, not an absolute container path. Hardcoding /src meant the scan ran
    # to completion, printed its table, and then died writing the file -- so the equity half never
    # ran at all when crypto went first.
    destination = (
        pathlib.Path(__file__).resolve().parent.parent
        / "artifacts"
        / f"scan_{label.split()[0].lower()}_{timeframe.lower()}.json"
    )
    destination.parent.mkdir(parents=True, exist_ok=True)
    with destination.open("w", encoding="utf-8") as handle:
        json.dump(
            {key: {"train_n": a[0], "train_mean": a[1], "test_n": b[0],
                   "test_mean": b[1], "test_lower": b[2], "trials": trials, "pbo": pbo}
             for key, a, b in rows},
            handle, indent=1)

    survivors = [r for r in rows if r[1][1] > 0 and r[2][2] > 0]
    print(f"  families positive in both halves at the 95% lower bound: {len(survivors)}")
    for key, _, (n2_, m2_, l2_, _, _) in survivors:
        print(f"     {key:32s} test n={n2_:<5d} mean={m2_:7.1f}  lower={l2_:7.1f}")


if __name__ == "__main__":
    # A coarser bar covers more calendar time for the same row limit, so the window widens with it
    # -- otherwise the 30-minute scan would run on a third of the sample and look noisier for that
    # reason alone rather than because the timeframe is worse.
    for timeframe, days in (("5Min", 60), ("15Min", 180), ("30Min", 360)):
        run(CRYPTO, CRYPTO_COST, "CRYPTO -- 7 pairs", session_scoped=False,
            timeframe=timeframe, days=days)
        run(EQUITY, EQUITY_COST, "US EQUITY ETFs -- 4 symbols", session_scoped=True,
            timeframe=timeframe, days=days)
