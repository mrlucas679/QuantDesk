"""Portfolio-level evaluation of a daily weight schedule.

Why this exists
---------------
The earlier equity experiments selected one ETF at a time, held it for a fixed number of
sessions, and then tested whether the *mean per-trade net return* was significantly positive.
That construction destroys statistical power three times over:

1. Holding a single asset carries the full single-asset variance. A 10-session ETF hold has a
   standard deviation near 230 bps.
2. Non-overlapping holds over the available history yield roughly 80 usable trades per phase.
3. The mean edge under test is on the order of tens of basis points.

Requiring a Bonferroni-corrected lower confidence bound above zero on 80 samples with a 230 bps
standard deviation needs an edge of roughly 75 bps per trade. No honest cross-sectional ETF
signal is that large, so the test rejected every candidate regardless of whether an edge was
present. The failure was in the measurement, not necessarily in the hypotheses.

Evaluating a *portfolio* fixes all three problems at once, without changing what the gates
require of a strategy. A
diversified, largely market-neutral weight schedule has a fraction of the single-asset variance,
and its return is observed every session rather than once per completed trade, so the same span
of history yields roughly 1,900 observations instead of 80.

Honesty requirements
--------------------
Two things must not be quietly gained in the change:

* **Costs.** A daily-rebalanced portfolio trades constantly. Cost is charged on realised
  turnover, ``sum(|w_t - w_{t-1}|)``, at the one-way scenario rate, every session. A signal that
  only works before turnover costs will not survive here.
* **Autocorrelation.** Slow signals produce serially correlated daily returns, so an
  independent-sample standard error would overstate significance — the exact direction of error
  that manufactures a false pass. Standard errors are therefore Newey-West (HAC) with a lag
  window at least as long as the signal's holding period.
"""

from __future__ import annotations

import math
from dataclasses import dataclass
from statistics import NormalDist

import numpy as np
import pandas as pd  # type: ignore[import-untyped]
from numpy.typing import NDArray

from quantdesk_research.backtest.equity_costs import EquityCostScenario

TRADING_DAYS_PER_YEAR = 252


@dataclass(frozen=True)
class PortfolioPerformance:
    """Costed, autocorrelation-aware evidence for one daily weight schedule."""

    observation_count: int
    mean_daily_net_bps: float
    mean_daily_gross_bps: float
    annualised_return_bps: float
    annualised_volatility_bps: float
    sharpe: float
    hac_standard_error_bps: float
    lower_confidence_daily_bps: float
    maximum_drawdown_bps: float
    average_daily_turnover: float
    annual_cost_bps: float
    first_half_mean_bps: float
    second_half_mean_bps: float
    positive_day_rate: float
    observations_required_for_significance: int

    @property
    def edge_per_risk(self) -> float:
        """Annualised net return divided by annualised volatility.

        This is the ranking objective the handoff asks for: expected net edge per unit of risk
        consumed, with opportunity frequency secondary. It equals the Sharpe ratio, restated to
        make the intent explicit at the call site.
        """
        return self.sharpe


def evaluate_weight_schedule(
    weights: pd.DataFrame,
    returns: pd.DataFrame,
    cost: EquityCostScenario,
    holding_days: int,
    alpha: float,
) -> PortfolioPerformance:
    """Score a daily weight schedule after turnover costs, with HAC standard errors.

    ``weights.loc[t]`` are the weights *held through session t*, and must be decided from
    information available before session t opens. ``returns.loc[t]`` are that session's simple
    returns. The caller owns the causality of the weights; this function does not shift them.

    ``alpha`` is the one-sided significance level *after* the caller's multiplicity correction.
    """
    if not 0.0 < alpha < 0.5:
        raise ValueError("alpha must be a one-sided significance level in (0, 0.5).")
    aligned_weights, aligned_returns = _align(weights, returns)
    if len(aligned_weights) < 2:
        raise ValueError("A weight schedule needs at least two aligned sessions to evaluate.")

    weight_values = aligned_weights.to_numpy(dtype=np.float64)
    return_values = aligned_returns.to_numpy(dtype=np.float64)
    gross = np.nansum(weight_values * return_values, axis=1)

    previous = np.vstack([np.zeros((1, weight_values.shape[1])), weight_values[:-1]])
    turnover = np.abs(weight_values - previous).sum(axis=1)
    costs = turnover * (cost.one_way_bps / 10_000.0)
    net = gross - costs

    return _summarise(net, gross, turnover, holding_days, cost, alpha)


def _align(weights: pd.DataFrame, returns: pd.DataFrame) -> tuple[pd.DataFrame, pd.DataFrame]:
    """Restrict both frames to their shared sessions and symbols, in a stable order."""
    symbols = sorted(set(weights.columns) & set(returns.columns))
    if not symbols:
        raise ValueError("Weights and returns share no symbols.")
    index = weights.index.intersection(returns.index).sort_values()
    return weights.loc[index, symbols].fillna(0.0), returns.loc[index, symbols].fillna(0.0)


def _summarise(
    net: NDArray[np.float64],
    gross: NDArray[np.float64],
    turnover: NDArray[np.float64],
    holding_days: int,
    cost: EquityCostScenario,
    alpha: float,
) -> PortfolioPerformance:
    """Reduce a daily net-return series to the evidence the gates consume."""
    count = len(net)
    mean = float(net.mean())
    deviation = float(net.std(ddof=1)) if count > 1 else 0.0
    standard_error = newey_west_standard_error(net, holding_days)
    sharpe = mean / deviation * math.sqrt(TRADING_DAYS_PER_YEAR) if deviation else 0.0

    cumulative = np.cumsum(net)
    drawdown = cumulative - np.maximum.accumulate(cumulative)
    midpoint = count // 2
    annual_turnover = float(turnover.mean()) * TRADING_DAYS_PER_YEAR

    return PortfolioPerformance(
        observation_count=count,
        mean_daily_net_bps=_bps(mean),
        mean_daily_gross_bps=_bps(float(gross.mean())),
        annualised_return_bps=_bps(mean * TRADING_DAYS_PER_YEAR),
        annualised_volatility_bps=_bps(deviation * math.sqrt(TRADING_DAYS_PER_YEAR)),
        sharpe=round(sharpe, 6),
        hac_standard_error_bps=_bps(standard_error),
        lower_confidence_daily_bps=_bps(mean - _critical_value(alpha) * standard_error),
        maximum_drawdown_bps=_bps(float(drawdown.min())),
        average_daily_turnover=round(float(turnover.mean()), 6),
        annual_cost_bps=round(annual_turnover * cost.one_way_bps, 6),
        first_half_mean_bps=_bps(float(net[:midpoint].mean())) if midpoint else 0.0,
        second_half_mean_bps=_bps(float(net[midpoint:].mean())) if midpoint else 0.0,
        positive_day_rate=round(float((net > 0).mean()), 6),
        observations_required_for_significance=observations_required(
            mean, standard_error, count, alpha
        ),
    )


def newey_west_standard_error(values: NDArray[np.float64], holding_days: int) -> float:
    """Return a heteroskedasticity- and autocorrelation-consistent standard error of the mean.

    A slow signal makes consecutive daily returns dependent. Treating them as independent would
    shrink the standard error and inflate significance, so the lag window is the larger of the
    Newey-West rule of thumb and the signal's own holding period.
    """
    count = len(values)
    if count < 2:
        return float("inf")
    rule_of_thumb = math.floor(4.0 * (count / 100.0) ** (2.0 / 9.0))
    lag = max(1, rule_of_thumb, holding_days)
    lag = min(lag, count - 1)

    centred = values - values.mean()
    variance = float(centred @ centred) / count
    total = variance
    for offset in range(1, lag + 1):
        covariance = float(centred[offset:] @ centred[:-offset]) / count
        total += 2.0 * (1.0 - offset / (lag + 1.0)) * covariance
    if total <= 0.0:
        # A negative HAC estimate is possible in small samples. Fall back to the independent
        # estimate rather than reporting an impossible standard error.
        total = variance
    return math.sqrt(total / count)


def observations_required(
    mean: float, standard_error: float, count: int, alpha: float
) -> int:
    """Return how many sessions this effect size would need to clear the confidence gate.

    This is the diagnostic that was missing. When a candidate fails only the confidence bound,
    this number says whether it failed because the edge is absent or because the sample is too
    small for the edge to be provable — two findings that demand opposite responses. Returns -1
    when the mean is not positive, because no sample size rescues a non-positive edge.
    """
    if mean <= 0 or standard_error <= 0 or count < 2:
        return -1
    deviation = standard_error * math.sqrt(count)
    required = (_critical_value(alpha) * deviation / mean) ** 2
    return math.ceil(required)


def _critical_value(alpha: float) -> float:
    return NormalDist().inv_cdf(1 - alpha)


def _bps(value: float) -> float:
    return round(value * 10_000.0, 6) if math.isfinite(value) else -1_000_000.0
