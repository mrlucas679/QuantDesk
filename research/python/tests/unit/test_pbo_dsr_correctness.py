"""The two statistics that license trading must fail when they should."""
import numpy as np
import pytest

from quantdesk_research.evaluation.deflated_sharpe import calculate_deflated_sharpe_ratio
from quantdesk_research.evaluation.pbo import calculate_pbo


def test_a_single_trial_is_not_automatically_certain_skill() -> None:
    # This returned 1.0 -- "certainly skilled" -- for any strategy whenever one was tested, which
    # inverts the statistic. With one trial it must collapse to the probabilistic Sharpe ratio,
    # so a weak Sharpe on a short sample still fails.
    weak = calculate_deflated_sharpe_ratio(
        observed_sharpe=0.01, n_trials=1, sharpe_variance=0.0, t_samples=100
    )
    strong = calculate_deflated_sharpe_ratio(
        observed_sharpe=0.30, n_trials=1, sharpe_variance=0.0, t_samples=100
    )

    assert weak < 0.9
    assert strong > weak


def test_a_shorter_sample_is_less_convincing_at_the_same_sharpe() -> None:
    common = {"observed_sharpe": 0.10, "n_trials": 1, "sharpe_variance": 0.0}
    short = calculate_deflated_sharpe_ratio(t_samples=30, **common)
    long = calculate_deflated_sharpe_ratio(t_samples=3000, **common)

    assert long > short


def test_a_degenerate_variance_fails_closed() -> None:
    # Extreme kurtosis drives the variance term non-positive; the normal approximation no longer
    # describes the sample, so the honest answer is no confidence rather than a number.
    assert calculate_deflated_sharpe_ratio(
        observed_sharpe=2.0, n_trials=4, sharpe_variance=0.01, t_samples=50, kurtosis=-400
    ) == 0.0


def test_too_few_observations_is_refused() -> None:
    with pytest.raises(ValueError, match="at least two observations"):
        calculate_deflated_sharpe_ratio(0.1, 4, 0.01, t_samples=1)


def test_pure_noise_reports_selection_as_worthless_or_worse() -> None:
    # With no real edge, selecting the in-sample best must not transfer. It lands *above* a coin
    # flip rather than at one, and that is a property of symmetric splits rather than a defect:
    # train and test halves are complementary, so a strategy lucky in one is arithmetically unlucky
    # in the other, and the in-sample winner is actively anti-selected out of sample.
    rng = np.random.default_rng(3)
    noise = rng.normal(0.0, 0.01, size=(1200, 12))

    pbo = calculate_pbo(noise, n_partitions=10)

    assert pbo >= 0.5, f"noise must not look like skill, got {pbo}"


def test_a_genuinely_persistent_edge_reports_low_overfitting() -> None:
    # One strategy really is better, consistently. Selection should then transfer out of sample.
    rng = np.random.default_rng(5)
    returns = rng.normal(0.0, 0.01, size=(1200, 8))
    returns[:, 0] += 0.004  # a large, stable edge

    pbo = calculate_pbo(returns, n_partitions=10)

    assert pbo < 0.2, f"a persistent edge should not look overfit, got {pbo}"


def test_the_statistic_separates_edge_from_noise() -> None:
    # The discriminating property, which is what makes the number worth acting on.
    rng = np.random.default_rng(21)
    noise = rng.normal(0.0, 0.01, size=(1200, 8))
    edged = noise.copy()
    edged[:, 0] += 0.004

    assert calculate_pbo(edged, n_partitions=10) < calculate_pbo(noise, n_partitions=10) - 0.3


def test_a_single_strategy_has_no_selection_to_measure() -> None:
    assert calculate_pbo(np.random.default_rng(1).normal(size=(500, 1))) == 0.0


def test_a_zero_variance_strategy_does_not_poison_the_ranking() -> None:
    # A constant column used to produce NaN Sharpes that lost every comparison silently.
    rng = np.random.default_rng(9)
    returns = rng.normal(0.0, 0.01, size=(600, 5))
    returns[:, 4] = 0.0

    assert 0.0 <= calculate_pbo(returns, n_partitions=8) <= 1.0
