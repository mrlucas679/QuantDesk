import numpy as np
from scipy import stats  # type: ignore[import-untyped]

from quantdesk_research.evaluation.trial_ledger import TrialLedger


def calculate_deflated_sharpe_ratio(
    observed_sharpe: float,
    n_trials: int,
    sharpe_variance: float,
    t_samples: int,
    skew: float = 0,
    kurtosis: float = 3,
) -> float:
    """
    Implements Deflated Sharpe Ratio (DSR).
    Reference: Bailey and Lopez de Prado (2014)
    """
    # A single trial is not evidence of skill; it is only the absence of a selection correction.
    #
    # This returned 1.0 -- "certainly skilled" -- whenever one strategy was tested, which inverts the
    # meaning of the statistic. With n_trials = 1 the expected maximum Sharpe under the null is simply
    # zero, so the deflated ratio collapses to the *probabilistic* Sharpe ratio: the probability the
    # true Sharpe exceeds zero given this sample's length, skew and kurtosis. A short or fat-tailed
    # sample must still be able to fail here.
    expected_max_sharpe = (
        0.0
        if n_trials <= 1
        else np.sqrt(sharpe_variance)
        * (
            (1 - np.euler_gamma) * stats.norm.ppf(1 - 1 / n_trials)
            + np.euler_gamma * stats.norm.ppf(1 - 1 / (n_trials * np.e))
        )
    )

    # Standard deviation of the Sharpe ratio
    if t_samples < 2:
        raise ValueError("The deflated Sharpe ratio needs at least two observations.")

    variance_term = (
        1
        + 0.5 * observed_sharpe**2
        - skew * observed_sharpe
        + (kurtosis - 3) / 4 * observed_sharpe**2
    ) / (t_samples - 1)

    # Heavy skew or kurtosis can drive the variance term non-positive, at which point the normal
    # approximation has stopped describing the sample. Failing closed is the only honest answer:
    # returning a high probability from a formula that no longer applies is exactly the kind of
    # false confidence this statistic exists to remove.
    if variance_term <= 0:
        return 0.0

    z = (observed_sharpe - expected_max_sharpe) / np.sqrt(variance_term)
    return float(stats.norm.cdf(z))


def calculate_dsr_from_ledger(
    hypothesis_family_id: str,
    observed_sharpe: float,
    t_samples: int,
    skew: float = 0,
    kurtosis: float = 3,
) -> float:
    """
    Utility to calculate DSR using trial count from the ledger.
    """
    ledger = TrialLedger()
    n_trials = ledger.get_trial_count(hypothesis_family_id)
    all_sharpes = ledger.get_all_sharpe_ratios(hypothesis_family_id)

    sharpe_variance = np.var(all_sharpes) if len(all_sharpes) > 1 else 0.0

    return calculate_deflated_sharpe_ratio(
        observed_sharpe=observed_sharpe,
        n_trials=max(n_trials, 1),
        sharpe_variance=sharpe_variance,
        t_samples=t_samples,
        skew=skew,
        kurtosis=kurtosis,
    )
