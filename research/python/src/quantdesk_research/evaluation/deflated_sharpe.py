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
    if n_trials <= 1:
        return 1.0  # No multiple testing to deflate against

    # Expected maximum Sharpe ratio under the null hypothesis (no skill)
    # approximated for large N
    expected_max_sharpe = np.sqrt(sharpe_variance) * (
        (1 - np.euler_gamma) * stats.norm.ppf(1 - 1 / n_trials)
        + np.euler_gamma * stats.norm.ppf(1 - 1 / (n_trials * np.e))
    )

    # Standard deviation of the Sharpe ratio
    sigma_sr = np.sqrt(
        (
            1
            + 0.5 * observed_sharpe**2
            - skew * observed_sharpe
            + (kurtosis - 3) / 4 * observed_sharpe**2
        )
        / (t_samples - 1)
    )

    z = (observed_sharpe - expected_max_sharpe) / sigma_sr
    dsr = stats.norm.cdf(z)

    return float(dsr)


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
