"""Fitting GARCH(1,1) with ``arch``, and exporting it so C# continues the same recursion.

The initialisation problem, and why warm-up beats carrying state
---------------------------------------------------------------
The recursion is stateful: sigma2[t+1] = omega + alpha*resid[t]**2 + beta*sigma2[t]. To evaluate it
you need a previous variance, and where that first value comes from is a real choice with a wrong
answer.

This code previously asserted that ``arch`` seeds the recursion from the unconditional variance.
It does not. ``arch`` backcasts: an exponentially weighted average of the first 75 squared
residuals with a decay of 0.94 (``arch/univariate/volatility.py``, ``GARCH.backcast``). Any
implementation seeding from omega / (1 - alpha - beta) starts on a different path from the fit it
claims to reproduce.

The obvious repair is to export the terminal fitted variance and have the runtime continue from it.
That is worse than it looks. It makes the artifact stateful, so a restart has to restore state that
may by then be hours stale, and it introduces a staleness policy nobody wants to own.

The measurement says neither is necessary. Because beta < 1, the influence of the seed decays
geometrically, and on a real fit (beta = 0.9175) every seed tried -- the backcast, the unconditional
variance, zero, and a deliberately absurd 1e6 -- converges to arch's own conditional variance path
to 1.1e-16 relative after 1000 bars. So the runtime does not need the seed and does not need the
state. It needs a warm-up window long enough that the seed cannot matter, and a refusal to forecast
before it has one.

``required_warmup_bars`` computes that length from beta rather than guessing it, and the parity
case is exactly this claim: feed the runtime a warm-up window of squared residuals and require it
to arrive at the conditional variance ``arch`` reports for the end of that window.

What this version refuses
-------------------------
A zero-mean GARCH(1,1) on p=1, o=0, q=1, power=2 and nothing else. Any other mean model means the
residual is not the return, and a runtime fed raw returns as innovations would be running the right
recursion on the wrong series. Refusing is cheap; a mean model reimplemented by inference is not.

Units are recorded, not assumed. A model fitted on percent returns cannot consume decimal returns
just because omega, alpha and beta survived serialisation -- omega is off by 1e4 and every forecast
with it is confidently, quietly wrong.
"""

from __future__ import annotations

import math
import warnings
from dataclasses import dataclass
from datetime import datetime

import arch
import numpy as np
from arch import arch_model
from numpy.typing import NDArray

from quantdesk_research.models.runtime_artifact import (
    FeatureSemantics,
    ParityCase,
    ParitySuite,
    ProducerIdentity,
    RuntimeInferenceArtifact,
    feature_schema_of,
    utc_now,
)

#: The only variant this bridge reproduces exactly.
SUPPORTED_SPECIFICATION = {"p": "1", "o": "0", "q": "1", "power": "2.0", "mean": "Zero"}

#: Return scales a fitted artifact may declare. The runtime must feed the same one.
SUPPORTED_RETURN_UNITS = frozenset({"decimal", "percent", "basis_points"})

FEATURE_NAMES = ["squared_residual"]

#: Below this the seed is lost in the last bits of the recursion, so any seed will do.
SEED_INFLUENCE_FLOOR = 1e-16

#: A warm-up beyond this is a model too persistent to be worth trusting on this timeframe.
MAXIMUM_WARMUP_BARS = 5000


class GarchFitRejected(Exception):
    """The fit produced something that must not become an artifact."""


@dataclass(frozen=True)
class GarchFit:
    """A fitted GARCH(1,1), with the diagnostics that decide whether it may be promoted."""

    omega: float
    alpha: float
    beta: float
    return_units: str
    warmup_bars: int
    converged: bool
    log_likelihood: float
    observations: int
    conditional_variance: NDArray[np.float64]
    squared_residuals: NDArray[np.float64]

    @property
    def persistence(self) -> float:
        return self.alpha + self.beta

    @property
    def unconditional_variance(self) -> float:
        return self.omega / (1.0 - self.persistence)


def required_warmup_bars(beta: float, floor: float = SEED_INFLUENCE_FLOOR) -> int:
    """How many bars until the seed's contribution falls below ``floor``.

    The seed enters the recursion multiplied by beta once per bar, so after n bars it is scaled by
    beta**n. Solving beta**n <= floor gives the point past which two runtimes seeding differently
    cannot disagree by an amount any tolerance would notice. Derived rather than guessed, because a
    round number chosen by taste is either wasteful on a fast-decaying fit or wrong on a persistent
    one.
    """
    if not 0.0 < beta < 1.0:
        raise GarchFitRejected(f"beta {beta} is outside (0, 1); the seed never stops mattering")
    return min(MAXIMUM_WARMUP_BARS, max(1, math.ceil(math.log(floor) / math.log(beta))))


def fit_garch(
    returns: NDArray[np.float64],
    *,
    return_units: str,
    minimum_observations: int = 500,
) -> GarchFit:
    """Fit a zero-mean GARCH(1,1), refusing every fit that cannot be reproduced or trusted."""
    if return_units not in SUPPORTED_RETURN_UNITS:
        raise GarchFitRejected(f"return_units must be one of {sorted(SUPPORTED_RETURN_UNITS)}")

    series = np.asarray(returns, dtype=np.float64)
    if series.ndim != 1:
        raise GarchFitRejected("returns must be one-dimensional")
    if not np.all(np.isfinite(series)):
        raise GarchFitRejected("returns contain non-finite values")
    if series.size < minimum_observations:
        raise GarchFitRejected(
            f"{series.size} observations is below the {minimum_observations} required"
        )

    # rescale=False explicitly. Left at its default, arch inspects the data scale and may warn or
    # rescale, which would put omega on a different scale than the runtime's inputs without either
    # side saying so.
    with warnings.catch_warnings():
        warnings.simplefilter("ignore")
        model = arch_model(series, mean="Zero", vol="GARCH", p=1, o=0, q=1, power=2.0, rescale=False)
        result = model.fit(disp="off", show_warning=False)

    omega = float(result.params["omega"])
    alpha = float(result.params["alpha[1]"])
    beta = float(result.params["beta[1]"])
    converged = int(result.convergence_flag) == 0

    if not converged:
        raise GarchFitRejected("the optimiser did not converge; its parameters are not a fit")
    if omega <= 0.0:
        raise GarchFitRejected(f"omega {omega} is not positive; the variance floor is degenerate")
    if alpha < 0.0 or beta < 0.0:
        raise GarchFitRejected(f"alpha {alpha} or beta {beta} is negative; this is not a GARCH fit")
    if alpha + beta >= 1.0:
        # No finite unconditional variance: forecasts diverge instead of reverting. A crisis or
        # trending window lands here regularly and the parameters look perfectly ordinary.
        raise GarchFitRejected(
            f"persistence {alpha + beta} is not below one; the process has no mean to revert to"
        )

    return GarchFit(
        omega=omega,
        alpha=alpha,
        beta=beta,
        return_units=return_units,
        warmup_bars=required_warmup_bars(beta),
        converged=converged,
        log_likelihood=float(result.loglikelihood),
        observations=int(series.size),
        conditional_variance=np.asarray(result.conditional_volatility, dtype=np.float64) ** 2.0,
        squared_residuals=series**2.0,
    )


def garch_parity_cases(fit: GarchFit, *, count: int = 3) -> list[ParityCase]:
    """Warm-up windows, with the conditional variance ``arch`` reports at the end of each.

    The expected value is read from ``result.conditional_volatility`` -- the library's own fitted
    path -- never recomputed here from the exported parameters. Recomputing it would test this
    module against itself and would pass just as happily if the recursion were wrong.

    Each case therefore asserts two things at once: that the runtime evaluates the recursion
    correctly, and that starting it cold at the window's beginning still lands on the path ``arch``
    reached from its own backcast. The second is the whole justification for not shipping state.
    """
    window = fit.warmup_bars
    if fit.observations <= window + count:
        raise GarchFitRejected(
            f"{fit.observations} observations cannot supply a {window}-bar warm-up parity window"
        )

    cases: list[ParityCase] = []
    for offset in range(count):
        end = fit.observations - 1 - offset
        start = end - window
        cases.append(
            ParityCase(
                inputs=[[float(value)] for value in fit.squared_residuals[start:end]],
                expected=[float(fit.conditional_variance[end])],
            )
        )
    return cases


def export_garch_artifact(
    fit: GarchFit,
    *,
    artifact_id: str,
    model_id: str,
    model_version: str,
    dataset_hash: str,
    git_commit: str,
    random_seed: int,
    as_of: datetime,
    bar_duration_minutes: int,
    evidence_grade: str = "B",
    promotion_state: str = "VALIDATED",
) -> RuntimeInferenceArtifact:
    """Seal the fit into the artifact the runtime loads, or refuse to."""
    schema = feature_schema_of(
        schema_version="garch11-zero-mean-v1",
        feature_names=FEATURE_NAMES,
        dtypes={"squared_residual": "float64"},
        normalization={},
        lookback_periods=fit.warmup_bars,
        source_requirements=["alpaca_ohlcv"],
    )

    artifact = RuntimeInferenceArtifact(
        artifact_id=artifact_id,
        model_id=model_id,
        model_family="garch",
        model_version=model_version,
        producer=ProducerIdentity(
            library="arch", library_version=arch.__version__, numpy_version=np.__version__
        ),
        feature_schema=schema,
        feature_schema_hash=schema.feature_hash,
        feature_semantics=FeatureSemantics(
            # Squared, so the unit is the square of the return unit. Spelled out because "percent"
            # on a variance is the ambiguity that puts omega off by four orders of magnitude.
            units={"squared_residual": f"squared_{fit.return_units}_return"},
            missing_policy="refuse",
            lookback_periods=fit.warmup_bars,
            bar_duration_minutes=bar_duration_minutes,
        ),
        dataset_hash=dataset_hash,
        parameters={"omega": fit.omega, "alpha": fit.alpha, "beta": fit.beta},
        variant={
            **SUPPORTED_SPECIFICATION,
            "return_units": fit.return_units,
            "warmup_bars": str(fit.warmup_bars),
            "horizon": "one_step",
        },
        random_seed=random_seed,
        evidence_grade=evidence_grade,
        promotion_state=promotion_state,
        diagnostics={
            "converged": fit.converged,
            "log_likelihood": fit.log_likelihood,
            "observations": fit.observations,
            "persistence": fit.persistence,
            "unconditional_variance": fit.unconditional_variance,
            "warmup_bars": fit.warmup_bars,
        },
        git_commit=git_commit,
        created_at=utc_now(),
        as_of=as_of,
        parity=ParitySuite(
            kind="sequence_to_vector",
            # A conditional variance on these scales is a small number, so relative is the
            # meaningful bound; the absolute floor only keeps a near-zero variance from failing on
            # its own last bits.
            absolute_tolerance=1e-18,
            relative_tolerance=1e-9,
            cases=garch_parity_cases(fit),
        ),
    )
    return artifact.sealed()
