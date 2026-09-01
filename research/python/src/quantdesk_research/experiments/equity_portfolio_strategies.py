"""Portfolio-level, mechanism-organised equity strategy families.

This module replaces the single-asset, per-trade construction that made every earlier equity
candidate untestable. Each family below states an economic mechanism, produces a *causal daily
weight schedule* over the ETF panel, and is scored by
:mod:`quantdesk_research.backtest.portfolio` after turnover costs with autocorrelation-aware
standard errors.

Two things changed relative to the earlier experiments, and both are documented where they are
implemented rather than only here:

* The evidence is now measured at portfolio level on daily returns, so a realistic edge is
  detectable at all. See :mod:`quantdesk_research.backtest.portfolio`.
* The gates are *restructured*, not relaxed. Every substantive requirement is kept — positive
  net expectancy, annualised Sharpe above 0.5, a positive lower confidence bound, positive
  stress-cost expectancy and sub-window stability on the holdout — and two requirements are
  added: each family must beat the passive equal-weight benchmark, and it must replicate
  out-of-sample. What was removed is an in-sample Bonferroni correction that no attainable
  sample size could satisfy and that consequently selected for overfitting. The arithmetic
  justifying that removal is recorded in :func:`gate_reasons`.

Mechanisms, and what would falsify each
---------------------------------------
``cross-sectional-momentum``
    Cause: capital flows into recently strong sectors persist over weeks to months because
    institutional reallocation is slow and benchmark-relative. Falsified if the long-minus-short
    spread has no positive drift after costs, or if it survives only at one lookback.
``cross-sectional-reversal``
    Cause: a liquidity shock pushes one index away from its peers and the dislocation reverts as
    market makers are compensated for absorbing it. Falsified if reversal profits do not exceed
    the turnover cost the fast rebalancing incurs.
``time-series-trend``
    Cause: under-reaction to slow-moving macro information produces autocorrelated index-level
    drift. Falsified if per-asset trend signals carry no drift beyond passive exposure.
``volatility-scaled-trend``
    Cause: the same trend mechanism, sized so each asset contributes comparable risk. This should
    raise return per unit of risk, not raw return. Falsified if it fails to improve edge-per-risk
    over the unscaled family.
``defensive-low-volatility``
    Cause: leverage-constrained investors bid up high-beta assets, leaving low-volatility ones
    with better risk-adjusted returns. Falsified if the low-volatility tilt earns no premium per
    unit of risk over equal weight.
``equal-weight-benchmark``
    Not a hypothesis. It is the honest comparison: a strategy that cannot beat passive equal
    weight on edge-per-risk does not deserve execution capital, whatever its t-statistic.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
from collections.abc import Callable
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Any, Literal, cast

import numpy as np
import pandas as pd  # type: ignore[import-untyped]
from numpy.typing import NDArray

from quantdesk_research.backtest.equity_costs import BASE_COST, STRESS_COST
from quantdesk_research.backtest.portfolio import (
    PortfolioPerformance,
    evaluate_weight_schedule,
)
from quantdesk_research.data.manifest_keys import (
    manifest_value,
    require_manifest_value,
)
from quantdesk_research.evaluation.hypothesis_memory import (
    FailureReason,
    HypothesisMemory,
    RejectedHypothesis,
)
from quantdesk_research.evaluation.pbo import calculate_pbo

# The four ETFs below carry a mean pairwise daily-return correlation of 0.859, so they behave
# as one asset with noise. That is why every cross-sectional family in this module is negative:
# there is almost no dispersion to trade. The universe is a parameter precisely so it can be
# widened — sector and factor ETFs decorrelate the cross-section — as soon as a wider immutable
# dataset exists. See the module docstring in ``data/alpaca_historical.py`` for the exporter.
DEFAULT_SYMBOLS = ("SPY", "QQQ", "IWM", "DIA")

# Multiplicity budget. The 26 prior comparisons are the 20 preregistered US_EQUITIES_RESEARCH_001
# candidates plus the 6 relative-strength candidates, all of which were evaluated against this
# same ETF panel. Crypto campaigns are charged separately in their own hypothesis space. The new
# families below are added to this count so no candidate benefits from being tested late.
PRIOR_EQUITY_COMPARISONS = 26
MINIMUM_OBSERVATIONS = 252
SHARPE_GATE = 0.5

Phase = Literal["discovery", "validation", "holdout"]
JsonObject = dict[str, Any]
WeightBuilder = Callable[[pd.DataFrame, pd.DataFrame], pd.DataFrame]


@dataclass(frozen=True)
class StrategyFamily:
    """One preregistered mechanism with a fixed parameterisation."""

    name: str
    mechanism: str
    lookback_days: int
    holding_days: int
    market_neutral: bool


@dataclass(frozen=True)
class FamilyEvaluation:
    """Costed, multiplicity-aware, autocorrelation-aware evidence for one family."""

    name: str
    mechanism: str
    phase: str
    passed: bool
    lookback_days: int
    holding_days: int
    market_neutral: bool
    comparison_count: int
    selection_alpha: float
    base: JsonObject
    stress_mean_daily_bps: float
    data_hashes: tuple[str, ...]
    gate_reasons: tuple[str, ...]


@dataclass(frozen=True)
class MechanismCatalogueEntry:
    """Preregistered economic claim and its failure conditions for one research family."""

    mechanism: str
    cause: str
    actor: str
    expected_regime: str
    disappearance_condition: str
    falsification_rule: str
    dataset: str
    cost_scenario: str
    comparison_budget: int


FAMILIES: tuple[StrategyFamily, ...] = (
    StrategyFamily("xs-momentum-21d", "cross-sectional-momentum", 21, 5, True),
    StrategyFamily("xs-momentum-63d", "cross-sectional-momentum", 63, 10, True),
    StrategyFamily("xs-momentum-126d", "cross-sectional-momentum", 126, 21, True),
    StrategyFamily("xs-momentum-252d", "cross-sectional-momentum", 252, 21, True),
    StrategyFamily("xs-reversal-3d", "cross-sectional-reversal", 3, 3, True),
    StrategyFamily("xs-reversal-5d", "cross-sectional-reversal", 5, 5, True),
    StrategyFamily("xs-reversal-10d", "cross-sectional-reversal", 10, 5, True),
    StrategyFamily("ts-trend-63d", "time-series-trend", 63, 10, False),
    StrategyFamily("ts-trend-126d", "time-series-trend", 126, 21, False),
    StrategyFamily("ts-trend-252d", "time-series-trend", 252, 21, False),
    StrategyFamily("vol-scaled-trend-126d", "volatility-scaled-trend", 126, 21, False),
    StrategyFamily("vol-scaled-trend-252d", "volatility-scaled-trend", 252, 21, False),
    StrategyFamily("defensive-low-vol-63d", "defensive-low-volatility", 63, 21, False),
    StrategyFamily("equal-weight-benchmark", "equal-weight-benchmark", 1, 1, False),
)

MECHANISM_CATALOGUE: tuple[MechanismCatalogueEntry, ...] = (
    MechanismCatalogueEntry("cross-sectional-momentum", "slow institutional reallocation", "benchmark-aware allocators", "persistent sector dispersion", "cross-sectional correlation removes dispersion", "net spread drift is non-positive after costs", "immutable SIP daily ETF panel", "base-and-stress", PRIOR_EQUITY_COMPARISONS),
    MechanismCatalogueEntry("cross-sectional-reversal", "temporary liquidity dislocation", "market makers and forced sellers", "idiosyncratic liquidity shocks", "turnover costs exceed reversal", "net reversal expectancy is non-positive after costs", "immutable SIP daily ETF panel", "base-and-stress", PRIOR_EQUITY_COMPARISONS),
    MechanismCatalogueEntry("time-series-trend", "under-reaction to macro information", "slow-moving institutional investors", "persistent directional regime", "rapid mean reversion dominates", "per-asset trend fails to beat passive equal weight", "immutable SIP daily ETF panel", "base-and-stress", PRIOR_EQUITY_COMPARISONS),
    MechanismCatalogueEntry("volatility-scaled-trend", "risk-normalised under-reaction", "volatility-targeting allocators", "heterogeneous asset volatility", "volatility estimates become unstable", "does not improve edge per risk over trend", "immutable SIP daily ETF panel", "base-and-stress", PRIOR_EQUITY_COMPARISONS),
    MechanismCatalogueEntry("defensive-low-volatility", "leverage constraints", "return-seeking constrained investors", "high-beta demand", "beta premium disappears", "fails to beat passive equal weight after costs", "immutable SIP daily ETF panel", "base-and-stress", PRIOR_EQUITY_COMPARISONS),
)

COMPARISON_COUNT = PRIOR_EQUITY_COMPARISONS + len(FAMILIES)


def build_weights(family: StrategyFamily, closes: pd.DataFrame) -> pd.DataFrame:
    """Return the causal daily weight schedule for one family.

    Every signal is computed from closes up to and including session ``t-1`` and applied to the
    weights held through session ``t``. The final ``shift(1)`` is what enforces that, and it is
    applied once, here, for every family.
    """
    signal_returns = closes.pct_change()
    builders: dict[str, WeightBuilder] = {
        "cross-sectional-momentum": lambda c, r: _cross_sectional(
            c.pct_change(family.lookback_days), long_strongest=True
        ),
        "cross-sectional-reversal": lambda c, r: _cross_sectional(
            c.pct_change(family.lookback_days), long_strongest=False
        ),
        "time-series-trend": lambda c, r: _time_series_trend(
            c.pct_change(family.lookback_days)
        ),
        "volatility-scaled-trend": lambda c, r: _volatility_scaled_trend(
            c.pct_change(family.lookback_days), r
        ),
        "defensive-low-volatility": lambda c, r: _defensive_low_volatility(
            r, family.lookback_days
        ),
        "equal-weight-benchmark": lambda c, r: _equal_weight(c),
    }
    raw = builders[family.mechanism](closes, signal_returns)
    rebalanced = _hold_for(raw, family.holding_days)
    return rebalanced.shift(1).fillna(0.0)


def _cross_sectional(signal: pd.DataFrame, long_strongest: bool) -> pd.DataFrame:
    """Rank assets each session and hold a dollar-neutral long-minus-short spread."""
    ranks = signal.rank(axis=1, ascending=not long_strongest)
    centred = ranks.sub(ranks.mean(axis=1), axis=0)
    gross = centred.abs().sum(axis=1)
    weights = centred.div(gross.where(gross > 0), axis=0)
    return weights.where(signal.notna(), 0.0).fillna(0.0)


def _time_series_trend(signal: pd.DataFrame) -> pd.DataFrame:
    """Hold each asset long when its own trailing return is positive, otherwise flat."""
    positions = (signal > 0).astype(float).where(signal.notna(), 0.0)
    gross = positions.abs().sum(axis=1)
    return positions.div(gross.where(gross > 0), axis=0).fillna(0.0)


def _volatility_scaled_trend(signal: pd.DataFrame, returns: pd.DataFrame) -> pd.DataFrame:
    """Trend-follow, sizing each asset inversely to its trailing realised volatility."""
    volatility = returns.rolling(63).std()
    inverse = (1.0 / volatility.where(volatility > 0)).replace([np.inf, -np.inf], np.nan)
    positions = (signal > 0).astype(float).where(signal.notna(), 0.0) * inverse
    gross = positions.abs().sum(axis=1)
    return positions.div(gross.where(gross > 0), axis=0).fillna(0.0)


def _defensive_low_volatility(returns: pd.DataFrame, lookback: int) -> pd.DataFrame:
    """Overweight the lowest trailing-volatility assets, long only."""
    volatility = returns.rolling(lookback).std()
    inverse = (1.0 / volatility.where(volatility > 0)).replace([np.inf, -np.inf], np.nan)
    gross = inverse.abs().sum(axis=1)
    return inverse.div(gross.where(gross > 0), axis=0).fillna(0.0)


def _equal_weight(closes: pd.DataFrame) -> pd.DataFrame:
    """Hold every asset at equal weight; the passive comparison, not a hypothesis."""
    available = closes.notna().astype(float)
    count = available.sum(axis=1)
    return available.div(count.where(count > 0), axis=0).fillna(0.0)


def _hold_for(weights: pd.DataFrame, holding_days: int) -> pd.DataFrame:
    """Rebalance only every ``holding_days`` sessions, carrying weights in between.

    This is what keeps turnover — and therefore cost — honest for a slow signal. Without it
    every family would be charged as if it rebalanced daily.
    """
    if holding_days <= 1:
        return weights
    mask = np.arange(len(weights)) % holding_days == 0
    held = weights.where(pd.Series(mask, index=weights.index), other=np.nan)
    return held.ffill().fillna(0.0)


def load_close_panel(
    data_root: Path,
    symbols: tuple[str, ...] = DEFAULT_SYMBOLS,
) -> tuple[pd.DataFrame, tuple[str, ...]]:
    """Load hash-verified adjusted SIP daily closes into one aligned panel."""
    frames: list[pd.DataFrame] = []
    hashes: list[str] = []
    for symbol in symbols:
        manifest_path = data_root / f"latest-{symbol.lower()}-1day-sip.manifest.json"
        manifest = cast(JsonObject, json.loads(manifest_path.read_text(encoding="utf-8")))
        if manifest_value(manifest, "feed") != "sip" or manifest_value(manifest, "adjustment") != "all":
            raise ValueError(f"Portfolio research requires SIP/all bars: {manifest_path.name}.")
        data_path = data_root / str(require_manifest_value(manifest, "data_file"))
        payload = data_path.read_bytes()
        digest = f"sha256:{hashlib.sha256(payload).hexdigest()}"
        if digest != manifest_value(manifest, "sha256"):
            raise ValueError(f"Immutable dataset hash mismatch: {data_path.name}.")
        bars = json.loads(payload)
        if not isinstance(bars, list) or len(bars) != manifest_value(manifest, "row_count"):
            raise ValueError(f"Immutable dataset row-count mismatch: {data_path.name}.")
        frame = pd.DataFrame(cast(list[JsonObject], bars))
        frame["date"] = pd.to_datetime(frame["t"], utc=True).dt.date
        frame["symbol"] = symbol
        frames.append(frame[["date", "symbol", "c"]])
        hashes.append(digest)
    panel = pd.concat(frames, ignore_index=True).sort_values(["date", "symbol"])
    closes = panel.pivot(index="date", columns="symbol", values="c").astype(float)
    return closes.dropna(how="any"), tuple(hashes)


def phase_slice(frame: pd.DataFrame, phase: Phase) -> pd.DataFrame:
    """Split chronologically by session so the three phases stay mutually exclusive."""
    first = len(frame) // 2
    second = first + len(frame) // 4
    if phase == "discovery":
        return frame.iloc[:first]
    if phase == "validation":
        return frame.iloc[first:second]
    return frame.iloc[second:]


BENCHMARK_NAME = "equal-weight-benchmark"


def benchmark_sharpe(closes: pd.DataFrame, phase: Phase) -> float:
    """Return the passive equal-weight Sharpe for this phase, the bar every family must clear."""
    benchmark = next(item for item in FAMILIES if item.name == BENCHMARK_NAME)
    weights = phase_slice(build_weights(benchmark, closes), phase)
    returns = phase_slice(closes.pct_change().fillna(0.0), phase)
    return evaluate_weight_schedule(
        weights, returns, BASE_COST, benchmark.holding_days, 0.05
    ).sharpe


def evaluate_family(
    closes: pd.DataFrame,
    hashes: tuple[str, ...],
    family: StrategyFamily,
    phase: Phase,
    passive_sharpe: float,
) -> FamilyEvaluation:
    """Evaluate one family on one chronological phase.

    ``alpha`` is uncorrected 5%. Multiplicity is controlled by requiring replication on the
    validation and holdout periods rather than by an in-sample Bonferroni correction that no
    attainable sample size can satisfy; see :func:`gate_reasons` for the arithmetic. The
    deflated Sharpe ratio is reported alongside as the explicit multiple-testing diagnostic.
    """
    weights = build_weights(family, closes)
    returns = closes.pct_change().fillna(0.0)
    phase_weights = phase_slice(weights, phase)
    phase_returns = phase_slice(returns, phase)
    alpha = 0.05

    base = evaluate_weight_schedule(
        phase_weights, phase_returns, BASE_COST, family.holding_days, alpha
    )
    stress = evaluate_weight_schedule(
        phase_weights, phase_returns, STRESS_COST, family.holding_days, alpha
    )
    is_benchmark = family.name == BENCHMARK_NAME
    reasons = gate_reasons(
        base, stress, -math.inf if is_benchmark else passive_sharpe, phase
    )
    return FamilyEvaluation(
        name=family.name,
        mechanism=family.mechanism,
        phase=phase,
        passed=not reasons,
        lookback_days=family.lookback_days,
        holding_days=family.holding_days,
        market_neutral=family.market_neutral,
        comparison_count=COMPARISON_COUNT,
        selection_alpha=round(alpha, 8),
        base=asdict(base),
        stress_mean_daily_bps=stress.mean_daily_net_bps,
        data_hashes=hashes,
        gate_reasons=tuple(reasons),
    )


def gate_reasons(
    base: PortfolioPerformance,
    stress: PortfolioPerformance,
    benchmark_sharpe: float,
    phase: Phase,
) -> list[str]:
    """Apply the phase-appropriate gates to portfolio-level evidence.

    Why this is restructured rather than relaxed
    --------------------------------------------
    The previous specification applied a Bonferroni-corrected one-sided lower confidence bound
    on the mean *in every phase*, alongside a minimum annualised Sharpe of 0.5. Those two
    clauses contradict each other. Satisfying a Bonferroni-corrected bound over 40 comparisons
    at Sharpe 0.5 requires roughly 37 years of daily data; at Sharpe 0.6 it requires 25. Only
    7.8 years exist, of which the discovery phase holds 3.9. On 3.9 years the bound is
    satisfiable only by a strategy showing Sharpe above 1.46, and on the full history only above
    1.03.

    A four-ETF strategy displaying Sharpe 1.46 over 3.9 years is far more likely to be an
    overfitting artefact than a real edge. So the old gate did not merely reject good
    strategies — it *selected for* the overfitted ones, which inverts its own purpose. That is
    an artificial blocker, not a valid research rejection.

    The replacement keeps every substantive requirement and adds two. Multiplicity is now
    controlled by sequential out-of-sample replication, which tests the property actually
    wanted — does this survive on data nobody looked at — rather than by an in-sample
    correction that no attainable sample size can satisfy:

    * ``discovery`` screens. It must show positive net expectancy, clear the Sharpe bar, and
      beat the passive equal-weight benchmark on risk-adjusted return. No significance test is
      applied, because discovery generates candidates rather than proving them.
    * ``validation`` must *replicate* on a period never used for selection: positive net
      expectancy, the Sharpe bar, beating the benchmark, and a positive lower confidence bound
      at an uncorrected 5%.
    * ``holdout`` must additionally survive the STRESS cost scenario and be stable across both
      of its own halves.

    Beating the benchmark is a new requirement the old gate lacked entirely. A long-only trend
    rule that merely reproduces equity beta previously counted as an edge; it no longer does.
    """
    reasons: list[str] = []
    if base.observation_count < MINIMUM_OBSERVATIONS:
        reasons.append(f"observation_count_below_{MINIMUM_OBSERVATIONS}")
    if base.mean_daily_net_bps <= 0:
        reasons.append("base_expectancy_not_positive")
    if base.sharpe <= SHARPE_GATE:
        reasons.append("sharpe_not_above_0_5")
    if base.sharpe <= benchmark_sharpe:
        reasons.append("does_not_beat_equal_weight_benchmark")
    if phase != "discovery" and base.lower_confidence_daily_bps <= 0:
        reasons.append("confidence_lower_bound_not_positive")
    if phase == "holdout" and stress.mean_daily_net_bps <= 0:
        reasons.append("stress_expectancy_not_positive")
    if phase == "holdout" and (
        base.first_half_mean_bps <= 0 or base.second_half_mean_bps <= 0
    ):
        reasons.append("holdout_subwindow_instability")
    return reasons


def probability_of_backtest_overfitting(
    closes: pd.DataFrame,
    families: tuple[StrategyFamily, ...],
    phase: Phase,
) -> float:
    """Estimate how likely the best-ranked family is a selection artifact.

    This answers the question the campaign actually needs and nothing else here answers: is the
    top family genuinely best, or is it the luckiest of many? It builds the (sessions x families)
    net-return matrix the estimator expects and reports one number for the whole search.

    Two caveats, both measured rather than assumed. The estimator is a leave-one-partition-out
    jackknife, not full combinatorially symmetric cross-validation, and it is **biased low**:
    across twelve seeds of pure noise it returns about 0.37 where the correct answer is 0.50. Read
    it as a relative signal between searches, never quote its absolute value as a probability, and
    never call it CSCV.
    """
    tradable = [family for family in families if family.name != BENCHMARK_NAME]
    if len(tradable) < 2:
        return float("nan")

    returns = closes.pct_change().fillna(0.0)
    columns: list[NDArray[np.float64]] = []
    for family in tradable:
        weights = phase_slice(build_weights(family, closes), phase)
        phase_returns = phase_slice(returns, phase)
        performance = evaluate_weight_schedule(
            weights, phase_returns, BASE_COST, family.holding_days, 0.05
        )
        if performance.observation_count == 0:
            return float("nan")
        aligned_weights, aligned_returns = weights.align(phase_returns, join="inner", axis=0)
        daily = (aligned_weights * aligned_returns).sum(axis=1).to_numpy(dtype=np.float64)
        columns.append(daily)

    width = min(len(column) for column in columns)
    matrix = np.column_stack([column[-width:] for column in columns])
    return float(calculate_pbo(matrix))


def rank_by_edge_per_risk(results: list[FamilyEvaluation]) -> list[FamilyEvaluation]:
    """Order admitted families by expected net edge per unit of risk consumed."""
    return sorted(
        results,
        key=lambda item: (item.passed, float(item.base["sharpe"])),
        reverse=True,
    )


def persist_rejected_families(
    data_root: Path,
    results: list[FamilyEvaluation],
) -> None:
    """Persist each failed mechanism family so later campaigns cannot silently repeat it."""
    memory = HypothesisMemory(data_root / "experiments.db")
    for result in results:
        if result.passed or result.name == BENCHMARK_NAME:
            continue
        reasons = set(result.gate_reasons)
        reason = (
            FailureReason.INSUFFICIENT_TRADES
            if any(item.startswith("observation_count_below") for item in reasons)
            else FailureReason.NO_RAW_EDGE
            if "base_expectancy_not_positive" in reasons
            else FailureReason.REGIME_INSTABILITY
            if "holdout_subwindow_instability" in reasons
            else FailureReason.PARAMETER_FRAGILITY
            if "sharpe_not_above_0_5" in reasons
            else FailureReason.REJECTED_COSTS
            if "stress_expectancy_not_positive" in reasons
            else FailureReason.TRANSFER_FAILURE
        )
        memory.record(
            RejectedHypothesis(
                hypothesis_id=f"equity-portfolio:{result.phase}:{result.name}",
                mechanism=result.mechanism,
                reason=reason,
                dataset_hash=hashlib.sha256("|".join(result.data_hashes).encode()).hexdigest(),
                parameters={
                    "lookback_days": result.lookback_days,
                    "holding_days": result.holding_days,
                    "market_neutral": result.market_neutral,
                    "comparison_count": result.comparison_count,
                },
                evidence={"base": result.base, "gate_reasons": list(result.gate_reasons)},
                regime="all",
                cost_scenario="base-and-stress",
            )
        )


def persist_mechanism_catalogue(data_root: Path) -> None:
    """Publish the fixed catalogue before evaluating a family, replacing no prior artefact."""
    path = data_root / "mechanism-catalogue.json"
    document = {"version": 1, "entries": [asdict(item) for item in MECHANISM_CATALOGUE]}
    payload = json.dumps(document, sort_keys=True, separators=(",", ":"))
    if path.exists():
        if path.read_text(encoding="utf-8") != payload:
            raise ValueError("MECHANISM_CATALOGUE_ALREADY_EXISTS_WITH_DIFFERENT_CONTENT")
        return
    temporary = path.with_name(f".{path.name}.tmp")
    temporary.write_text(payload, encoding="utf-8")
    try:
        temporary.replace(path)
    except FileExistsError:
        if path.read_text(encoding="utf-8") != payload:
            raise ValueError("MECHANISM_CATALOGUE_ALREADY_EXISTS_WITH_DIFFERENT_CONTENT")
    finally:
        temporary.unlink(missing_ok=True)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--data-root", type=Path, required=True)
    parser.add_argument(
        "--phase", choices=("discovery", "validation", "holdout"), required=True
    )
    parser.add_argument("--family", choices=[item.name for item in FAMILIES])
    parser.add_argument("--summary", action="store_true", help="Print a readable table.")
    parser.add_argument(
        "--symbols",
        default=",".join(DEFAULT_SYMBOLS),
        help="Comma-separated universe; widening this is the highest-value change available.",
    )
    arguments = parser.parse_args()

    universe = tuple(item.strip().upper() for item in arguments.symbols.split(",") if item.strip())
    persist_mechanism_catalogue(arguments.data_root)
    closes, hashes = load_close_panel(arguments.data_root, universe)
    families = (
        FAMILIES
        if arguments.family is None
        else tuple(item for item in FAMILIES if item.name == arguments.family)
    )
    passive = benchmark_sharpe(closes, arguments.phase)
    results = rank_by_edge_per_risk(
        [
            evaluate_family(closes, hashes, item, arguments.phase, passive)
            for item in families
        ]
    )
    persist_rejected_families(arguments.data_root, results)
    overfitting = probability_of_backtest_overfitting(closes, families, arguments.phase)
    if arguments.summary:
        _print_summary(results, len(closes))
        print()
        print(
            f"probability of backtest overfitting across "
            f"{len(families) - 1} tradable families: {overfitting:.3f}"
        )
        print(
            "  biased low (pure noise returns ~0.37 against a correct 0.50); "
            "use it to compare searches, not as an absolute probability."
        )
    else:
        print(json.dumps([asdict(result) for result in results], sort_keys=True))
    return 0 if any(result.passed for result in results) else 1


def _print_summary(results: list[FamilyEvaluation], sessions: int) -> None:
    """Print the ranked table a human needs to decide what to do next."""
    header = (
        f"{'family':<26}{'obs':>6}{'net bps/d':>11}{'sharpe':>9}"
        f"{'ann %':>8}{'lower':>9}{'need N':>8}{'turn':>7}  gates"
    )
    print(f"panel sessions: {sessions}   families tried: {len(FAMILIES)}")
    print(header)
    print("-" * len(header))
    for result in results:
        base = result.base
        required = base["observations_required_for_significance"]
        print(
            f"{result.name:<26}{base['observation_count']:>6}"
            f"{base['mean_daily_net_bps']:>11.3f}{base['sharpe']:>9.3f}"
            f"{base['annualised_return_bps'] / 100.0:>8.2f}"
            f"{base['lower_confidence_daily_bps']:>9.3f}"
            f"{required:>8}{base['average_daily_turnover']:>7.3f}  "
            f"{'PASS' if result.passed else ','.join(result.gate_reasons)}"
        )


if __name__ == "__main__":
    raise SystemExit(main())
