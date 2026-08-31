from __future__ import annotations

import numpy as np
import pandas as pd  # type: ignore[import-untyped]
import pytest

from quantdesk_research.backtest.portfolio import PortfolioPerformance
from quantdesk_research.experiments.equity_portfolio_strategies import (
    FAMILIES,
    SHARPE_GATE,
    build_weights,
    gate_reasons,
    phase_slice,
    rank_by_edge_per_risk,
)


def _closes(periods: int = 400) -> pd.DataFrame:
    rng = np.random.default_rng(20260831)
    index = pd.RangeIndex(periods)
    data = {
        symbol: 100.0 * np.cumprod(1.0 + rng.normal(0.0004, 0.01, periods))
        for symbol in ("SPY", "QQQ", "IWM", "DIA")
    }
    return pd.DataFrame(data, index=index)


@pytest.mark.parametrize("family", FAMILIES, ids=[item.name for item in FAMILIES])
def test_every_family_produces_causal_bounded_weights(family) -> None:  # type: ignore[no-untyped-def]
    closes = _closes()
    weights = build_weights(family, closes)

    assert list(weights.index) == list(closes.index)
    # The first session cannot be positioned: its signal is not knowable before it opens.
    assert weights.iloc[0].abs().sum() == 0.0
    # Gross exposure is normalised to at most one unit, so cost is comparable across families.
    assert weights.abs().sum(axis=1).max() <= 1.0 + 1e-9
    assert not weights.isna().to_numpy().any()


def test_market_neutral_families_hold_no_net_exposure() -> None:
    closes = _closes()
    for family in FAMILIES:
        if not family.market_neutral:
            continue
        weights = build_weights(family, closes)
        net = weights.sum(axis=1).abs().max()
        assert net < 1e-9, f"{family.name} is declared market neutral but holds net exposure."


def test_weights_ignore_future_prices() -> None:
    """Changing a future close must not alter any weight held on or before that session."""
    closes = _closes()
    family = next(item for item in FAMILIES if item.name == "ts-trend-126d")
    baseline = build_weights(family, closes)

    tampered = closes.copy()
    tampered.iloc[300:] *= 1.5
    perturbed = build_weights(family, tampered)

    pd.testing.assert_frame_equal(baseline.iloc[:301], perturbed.iloc[:301])


def test_phases_are_chronological_and_mutually_exclusive() -> None:
    frame = _closes(400)
    discovery = phase_slice(frame, "discovery")
    validation = phase_slice(frame, "validation")
    holdout = phase_slice(frame, "holdout")

    assert len(discovery) + len(validation) + len(holdout) == len(frame)
    assert discovery.index.max() < validation.index.min()
    assert validation.index.max() < holdout.index.min()


def _performance(**overrides: float) -> PortfolioPerformance:
    defaults: dict[str, float] = {
        "observation_count": 500,
        "mean_daily_net_bps": 5.0,
        "mean_daily_gross_bps": 6.0,
        "annualised_return_bps": 1260.0,
        "annualised_volatility_bps": 1600.0,
        "sharpe": 0.8,
        "hac_standard_error_bps": 2.0,
        "lower_confidence_daily_bps": 1.0,
        "maximum_drawdown_bps": -500.0,
        "average_daily_turnover": 0.01,
        "annual_cost_bps": 5.0,
        "first_half_mean_bps": 4.0,
        "second_half_mean_bps": 6.0,
        "positive_day_rate": 0.54,
        "observations_required_for_significance": 300,
    }
    defaults.update(overrides)
    return PortfolioPerformance(**defaults)  # type: ignore[arg-type]


def test_discovery_screens_without_requiring_significance() -> None:
    strong = _performance(lower_confidence_daily_bps=-1.0)

    assert gate_reasons(strong, strong, 0.4, "discovery") == []
    # The same evidence must not clear validation, which demands replication.
    assert "confidence_lower_bound_not_positive" in gate_reasons(strong, strong, 0.4, "validation")


def test_a_family_that_cannot_beat_passive_is_rejected_in_every_phase() -> None:
    performance = _performance(sharpe=0.8)

    for phase in ("discovery", "validation", "holdout"):
        reasons = gate_reasons(performance, performance, 0.9, phase)  # type: ignore[arg-type]
        assert "does_not_beat_equal_weight_benchmark" in reasons


def test_sharpe_gate_and_expectancy_gate_are_retained() -> None:
    weak = _performance(sharpe=SHARPE_GATE)
    assert "sharpe_not_above_0_5" in gate_reasons(weak, weak, 0.1, "discovery")

    negative = _performance(mean_daily_net_bps=-0.1)
    assert "base_expectancy_not_positive" in gate_reasons(negative, negative, 0.1, "discovery")


def test_holdout_adds_stress_and_stability_requirements() -> None:
    base = _performance()
    failing_stress = _performance(mean_daily_net_bps=-1.0)
    assert "stress_expectancy_not_positive" in gate_reasons(base, failing_stress, 0.1, "holdout")

    unstable = _performance(first_half_mean_bps=-2.0)
    assert "holdout_subwindow_instability" in gate_reasons(unstable, unstable, 0.1, "holdout")


def test_short_samples_are_rejected() -> None:
    short = _performance(observation_count=100)
    assert any(reason.startswith("observation_count_below") for reason in
               gate_reasons(short, short, 0.1, "discovery"))


def test_ranking_puts_passing_families_first_then_highest_edge_per_risk() -> None:
    from quantdesk_research.experiments.equity_portfolio_strategies import FamilyEvaluation

    def evaluation(name: str, passed: bool, sharpe: float) -> FamilyEvaluation:
        return FamilyEvaluation(
            name=name, mechanism="m", phase="discovery", passed=passed,
            lookback_days=1, holding_days=1, market_neutral=False,
            comparison_count=1, selection_alpha=0.05,
            base={"sharpe": sharpe}, stress_mean_daily_bps=0.0,
            data_hashes=(), gate_reasons=(),
        )

    ranked = rank_by_edge_per_risk([
        evaluation("weak-pass", True, 0.6),
        evaluation("strong-fail", False, 2.0),
        evaluation("strong-pass", True, 1.2),
    ])

    assert [item.name for item in ranked] == ["strong-pass", "weak-pass", "strong-fail"]
