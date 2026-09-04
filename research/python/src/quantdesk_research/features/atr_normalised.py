"""Features expressed in units of the instrument's own current volatility.

What was wrong with the old set
-------------------------------
The existing feature vector is raw quantities: ``return_1``, ``return_12``, ``vwap_distance``,
``range_fraction``. Each is a number whose *meaning* changes with the regime. A 40 bps 15-minute
return is a violent move in a quiet week and unremarkable in a volatile one, and a model fed the raw
number has to learn that context from other columns -- which is precisely what a small, noisy panel
cannot do reliably.

So the model spends its capacity re-learning the volatility scale instead of learning the signal,
and a coefficient fitted in one regime is wrong in the next. That is the concrete sense in which the
models were not catching the right thing.

The fix, from the handbook's own worked example
-----------------------------------------------
Divide by ATR. Instead of::

    price = 60600, sma = 60300

feed::

    price_sma_gap_atr = 1.026

which says "price is one ATR above its own average" and means the same thing on BTC at $60,000, on
UNI at $6, in a calm week and in a crisis. Every distance here is in ATR units, every ratio is
already scale-free, and the target is normalised the same way so the model is asked a question whose
answer does not drift.

Multi-horizon by construction
-----------------------------
Returns are computed over several spans of the primary bar -- one, two, four, sixteen and a full day
-- so a 15-minute primary clock carries its own 30-minute, 1-hour, 4-hour and daily context without
a second data feed. That is the composite the handbook uses for trend, and it is cheaper and better
aligned than subscribing to four separate candle streams that mostly repeat each other.
"""

from __future__ import annotations

import numpy as np
import pandas as pd

#: Wilder's ATR window. Fourteen is the convention and is not tuned here; tuning a window on the
#: same data used to judge the features is how a comparison flatters itself.
ATR_PERIOD = 14

#: Return horizons, in bars of the primary clock. On 15-minute bars these are 15m, 30m, 1h, 4h and
#: one day; on 30-minute bars they are 30m, 1h, 2h, 8h and one day.
RETURN_SPANS = (1, 2, 4, 16, 96)

FEATURE_NAMES: tuple[str, ...] = (
    "ret_1_atr",
    "ret_2_atr",
    "ret_4_atr",
    "ret_16_atr",
    "ret_96_atr",
    "trend_agreement",
    "price_sma_gap_atr",
    "fast_slow_gap_atr",
    "breakout_atr",
    "vwap_gap_atr",
    "atr_pct",
    "atr_ratio",
    "volume_z",
    "prev_range_position",
)


def true_range(high: pd.Series, low: pd.Series, close: pd.Series) -> pd.Series:
    """TR = max(H-L, |H-C_prev|, |L-C_prev|), which is the handbook's Step 5 definition.

    The two gap terms are what distinguish true range from a bare high-low: an instrument that opens
    away from yesterday's close has moved, and a range that ignores the gap understates it.
    """
    previous = close.shift(1)
    return pd.concat(
        [high - low, (high - previous).abs(), (low - previous).abs()], axis=1
    ).max(axis=1)


def build(frame: pd.DataFrame, horizon_bars: int) -> pd.DataFrame:
    """Attach ATR-normalised features and a normalised target to a bar frame.

    ``frame`` carries the venue's own column names -- t, o, h, l, c, v, and vw where present -- and
    must be sorted ascending with no duplicate timestamps. Every feature reads only bars at or
    before its own row: the shift(1) on the ATR baseline and on the breakout high is what keeps the
    current bar out of its own reference, which is the difference between a feature and a leak.
    """
    out = frame.sort_values("t").drop_duplicates("t").reset_index(drop=True).copy()
    close = out["c"].astype(float)
    high = out["h"].astype(float)
    low = out["l"].astype(float)
    log_close = np.log(close)

    # Wilder's smoothing, which decays at 1/n rather than an EMA's 2/(n+1).
    atr = true_range(high, low, close).ewm(alpha=1 / ATR_PERIOD, adjust=False).mean()

    # The denominator for every distance below. Lagged by one bar so a feature is measured against
    # volatility that was already known when the bar opened, not against volatility this bar itself
    # created -- a breakout bar inflates its own ATR and would otherwise deflate its own score.
    scale = (atr.shift(1) / close.shift(1)).replace(0.0, np.nan)

    for span in RETURN_SPANS:
        out[f"ret_{span}_atr"] = (log_close.diff(span) / (scale * np.sqrt(span))).astype(float)

    # The handbook's composite trend signal: the mean of the signs across horizons, which is +1 when
    # every clock agrees and near zero when they disagree. A single number carrying agreement is
    # what the worked example reaches at Step 4, where 15m, 1h and 1d all read +1.
    out["trend_agreement"] = np.mean(
        [np.sign(log_close.diff(span)) for span in RETURN_SPANS], axis=0
    )

    sma_slow = close.rolling(16).mean()
    sma_fast = close.rolling(4).mean()
    out["price_sma_gap_atr"] = ((close - sma_slow) / close / scale).astype(float)
    out["fast_slow_gap_atr"] = ((sma_fast - sma_slow) / close / scale).astype(float)

    # Distance beyond the prior window's extreme, signed, in ATR. Positive is a breakout above,
    # negative a breakdown below -- one feature carrying both, so a model is not forced to learn
    # that two indicator columns are halves of one idea.
    prior_high = high.rolling(16).max().shift(1)
    prior_low = low.rolling(16).min().shift(1)
    above = (close - prior_high).clip(lower=0)
    below = (close - prior_low).clip(upper=0)
    out["breakout_atr"] = ((above + below) / close / scale).astype(float)

    vwap = out["vw"].astype(float) if "vw" in out else close.rolling(48).mean()
    out["vwap_gap_atr"] = ((close - vwap) / close / scale).astype(float)

    # Volatility as a level and as a change. The level says how far this instrument moves; the ratio
    # says whether it is currently moving more or less than it usually does, which is the regime
    # question asked without a regime model.
    out["atr_pct"] = (atr / close).astype(float)
    out["atr_ratio"] = (atr / atr.rolling(96).mean()).astype(float)

    volume = out["v"].astype(float)
    baseline = volume.rolling(48)
    out["volume_z"] = ((volume - baseline.mean()) / baseline.std()).astype(float)

    # Where price sits inside the previous day's range: below 0 is under yesterday's low, above 1 is
    # above its high. The handbook's Step "should we use yesterday's data" -- as context, not as the
    # entry signal.
    day_high = high.rolling(96).max().shift(96)
    day_low = low.rolling(96).min().shift(96)
    span = (day_high - day_low).replace(0.0, np.nan)
    out["prev_range_position"] = ((close - day_low) / span).astype(float)

    # The target is normalised the same way the features are. Predicting a raw forward return asks
    # the model for a quantity whose scale moves with the regime; predicting it in ATR units asks
    # for the same question in every regime. The raw return is kept alongside because P&L is
    # denominated in returns, not in ATRs, and the evaluation has to charge costs in basis points.
    forward = log_close.shift(-horizon_bars) - log_close
    out["target_return"] = forward.astype(float)
    out["target_atr"] = (forward / (scale * np.sqrt(horizon_bars))).astype(float)

    return out.replace([np.inf, -np.inf], np.nan)


def frame_for(bars: list[dict[str, object]], horizon_bars: int) -> pd.DataFrame:
    """Feature rows whose forward label is fully observed, ready to score."""
    built = build(pd.DataFrame(bars), horizon_bars)
    return built.dropna(subset=[*FEATURE_NAMES, "target_return"]).reset_index(drop=True)
