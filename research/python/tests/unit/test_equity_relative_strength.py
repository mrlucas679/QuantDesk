import pandas as pd  # type: ignore[import-untyped]

from quantdesk_research.experiments.equity_relative_strength import (
    CANDIDATES,
    PRIOR_COMPARISONS,
    candidate_returns,
    chronological_phase,
)


def test_candidate_uses_next_session_open_and_non_overlapping_exit() -> None:
    dates = pd.date_range("2026-01-01", periods=150, freq="B").date
    rows = []
    for symbol, drift in (("SPY", 1.001), ("QQQ", 1.002), ("IWM", 0.999), ("DIA", 1.0)):
        close = 100.0
        for date in dates:
            close *= drift
            rows.append({"date": date, "symbol": symbol, "o": close / drift, "c": close})
    panel = pd.DataFrame(rows)

    returns = candidate_returns(panel, CANDIDATES[0])

    assert len(returns) <= (len(dates) - CANDIDATES[0].lookback_days) // 5 + 1
    assert (returns > 0).all()


def test_chronological_phases_are_disjoint_and_complete() -> None:
    values = pd.Series(range(40), dtype=float)

    discovery = chronological_phase(values, "discovery")
    validation = chronological_phase(values, "validation")
    holdout = chronological_phase(values, "holdout")

    assert set(discovery).isdisjoint(validation)
    assert set(discovery).isdisjoint(holdout)
    assert set(validation).isdisjoint(holdout)
    assert len(discovery) + len(validation) + len(holdout) == len(values)


def test_new_campaign_charges_prior_and_registered_comparisons() -> None:
    assert PRIOR_COMPARISONS == 20
    assert PRIOR_COMPARISONS + len(CANDIDATES) == 26
