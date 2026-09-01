"""Selection-free ensembles, and the multiple-testing correction that was only claimed."""
import numpy as np
import pandas as pd

from quantdesk_research.evaluation.deflated_sharpe import calculate_deflated_sharpe_ratio
from quantdesk_research.experiments.equity_portfolio_strategies import (
    FAMILIES,
    build_weights,
)


def _closes(sessions: int = 600) -> pd.DataFrame:
    rng = np.random.default_rng(11)
    steps = rng.normal(0.0004, 0.01, size=(sessions, 4))
    prices = 100 * np.exp(np.cumsum(steps, axis=0))
    return pd.DataFrame(prices, columns=["DIA", "IWM", "QQQ", "SPY"])


def _family(name: str):
    return next(item for item in FAMILIES if item.name == name)


def test_the_ensembles_exist_and_are_not_hypotheses_of_their_own() -> None:
    # Their membership is structural, so they cannot be "tuned" -- that is the entire point.
    names = {item.name for item in FAMILIES}
    assert {"ensemble-all", "ensemble-directional"} <= names


def test_an_ensemble_holds_a_full_book_rather_than_a_diluted_one() -> None:
    # Dollar-neutral and long-only constituents partly cancel when averaged. Without
    # renormalisation the ensemble would run a smaller book and report a flattered
    # risk-adjusted return purely for that reason.
    weights = build_weights(_family("ensemble-all"), _closes())
    gross = weights.abs().sum(axis=1)
    active = gross[gross > 0]

    assert len(active) > 0
    assert np.allclose(active.to_numpy(), 1.0, atol=1e-9)


def test_the_directional_ensemble_is_long_only() -> None:
    weights = build_weights(_family("ensemble-directional"), _closes())
    assert (weights >= -1e-12).all().all()


def test_an_ensemble_differs_from_every_constituent() -> None:
    # If it matched one, averaging would be doing nothing and the selection-bias argument
    # would be hollow.
    closes = _closes()
    ensemble = build_weights(_family("ensemble-directional"), closes)
    for name in ("ts-trend-126d", "vol-scaled-trend-252d", "defensive-low-vol-63d"):
        assert not np.allclose(ensemble.to_numpy(), build_weights(_family(name), closes).to_numpy())


def test_deflation_penalises_a_wider_search() -> None:
    # The property that matters: the same observed Sharpe is worth less when more were tried.
    common = {"observed_sharpe": 0.08, "sharpe_variance": 0.0009, "t_samples": 1000}
    few = calculate_deflated_sharpe_ratio(n_trials=2, **common)
    many = calculate_deflated_sharpe_ratio(n_trials=200, **common)

    assert many < few


def test_a_single_trial_is_not_deflated() -> None:
    assert calculate_deflated_sharpe_ratio(0.08, 1, 0.0009, 1000) == 1.0
