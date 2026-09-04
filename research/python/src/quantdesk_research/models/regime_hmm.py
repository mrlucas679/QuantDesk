"""Fitting a Gaussian HMM with ``hmmlearn``, and exporting the filtered posterior C# reproduces.

The parity trap that makes this the hardest of the four
-------------------------------------------------------
The runtime filters: given the belief carried from the last bar and a new observation, what is the
posterior over states now. It has no future data, because it is running live.

``hmmlearn`` exposes ``predict_proba``, which looks like exactly that and is not. It returns the
*smoothed* posterior from a full forward-backward pass, in which every timestep is informed by the
observations that came after it. Feeding a sequence and taking the answers row by row would produce
expected values the runtime cannot reproduce and should not: they use information it will never
have.

What makes this genuinely dangerous rather than merely wrong is where the two agree. At the final
row of a sequence the backward message is uniform, so the smoothed posterior *equals* the filtered
one. A spot-check of the last row passes. Every row before it is wrong. Measured on a three-state
fit, rows 1 and 6 of a six-observation sequence matched and rows 2 through 5 did not.

The way out keeps the library as the only oracle: to get the filtered posterior at step k, call
``predict_proba`` on the prefix ``sequence[:k + 1]`` and take its last row. Each call is a full
forward-backward pass whose last row is, by that same identity, the filtered posterior. Nothing is
reimplemented here, and the transition matrix is exercised because the prefix has a history.

``score`` gives a second, independent anchor: the log-likelihood of a sequence is the sum of the
forward pass normalisers, so a filter that reproduces the posteriors but not that total has got the
recursion right and the normalisation wrong.

State identity across retrains
------------------------------
State 0 from today's fit is not the same economic regime as state 0 from tomorrow's. Baum-Welch has
no notion of which state is "calm"; the labels fall out of the initialisation. Left alone, a
retrain that merely permutes the numbering makes every downstream regime-change interrupt fire on a
change that did not happen. So states are sorted into a canonical order by a designated feature's
fitted mean before anything is exported, and the parity cases are generated from the sorted model.

The covariance shape trap
-------------------------
``model.covars_`` is a property that calls ``fill_covars``, which expands a diagonal fit into full
matrices: for ``covariance_type='diag'`` it returns (n_states, n_features, n_features), not
(n_states, n_features). Reading whatever shape appears and calling it the wire format would export
a matrix where a vector was meant. The diagonal is taken explicitly here.
"""

from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime

import hmmlearn  # type: ignore[import-untyped]
import numpy as np
from hmmlearn import hmm
from numpy.typing import NDArray

from quantdesk_research.models.runtime_artifact import (
    SupportDomain,
    FeatureSemantics,
    ParityCase,
    ParitySuite,
    ProducerIdentity,
    RuntimeInferenceArtifact,
    feature_schema_of,
    utc_now,
)

#: Covariance shapes the C# filter reproduces exactly. Full and tied need a factorisation whose
#: conditioning behaviour would have to match another library's, and an approximated regime model
#: is worse than none because the exit engine acts on it.
SUPPORTED_COVARIANCE_TYPES = frozenset({"diag", "spherical"})

#: A state visited less often than this over the training window is not a regime, it is an artefact
#: of initialisation, and its fitted mean and variance are estimated from almost nothing.
MINIMUM_STATE_OCCUPANCY = 0.02

#: How far apart two states' means must sit, in pooled standard deviations, on at least one
#: feature.
#:
#: Occupancy alone does not catch an over-specified model, which was not obvious until it was
#: measured: six states fitted to a single Gaussian blob converge, validate, and come back with
#: occupancy near 1/6 each. Nothing is degenerate -- the model has simply carved one distribution
#: into six arbitrary slices, and every slice is well estimated. Asking whether the states are
#: distinguishable is a different question from asking whether they are populated, and it is the
#: one that decides whether "the regime changed" means anything.
MINIMUM_STATE_SEPARATION = 1.0

#: How far the forward normalisers may drift from the library's own sequence log-likelihood.
SCORE_ANCHOR_TOLERANCE = 1e-9


class HmmFitRejected(Exception):
    """The fit produced something that must not become an artifact."""


@dataclass(frozen=True)
class RegimeHmmFit:
    """A fitted Gaussian HMM in canonical state order, with its promotion diagnostics."""

    model: hmm.GaussianHMM
    covariance_type: str
    feature_names: list[str]
    state_labels: list[str]
    occupancy: NDArray[np.float64]
    log_likelihood: float
    converged: bool
    restarts: int
    observations: int
    training_sequence: NDArray[np.float64]

    @property
    def n_states(self) -> int:
        return int(self.model.n_components)

    @property
    def n_features(self) -> int:
        return int(self.model.n_features)

    def variances(self) -> NDArray[np.float64]:
        """The per-state, per-feature variances, taken from the diagonal explicitly.

        Never from ``covars_`` directly: that property expands a diagonal fit to full matrices, so
        the naive read exports the wrong shape without failing.
        """
        return np.asarray(np.diagonal(self.model.covars_, axis1=1, axis2=2), dtype=np.float64)


def _minimum_separation(
    means: NDArray[np.float64], variances: NDArray[np.float64]
) -> float:
    """How far the closest pair of states sits apart, in pooled standard deviations.

    Measured per feature and taken at the widest, because two regimes need only be distinguishable
    on *something*: a calm and a stressed state can share an identical trend reading and differ
    entirely in volatility, and averaging across features would hide that.
    """
    states = means.shape[0]
    closest = float("inf")
    for i in range(states):
        for j in range(i + 1, states):
            pooled = np.sqrt((variances[i] + variances[j]) / 2.0)
            widest = float(np.max(np.abs(means[i] - means[j]) / pooled))
            closest = min(closest, widest)
    return closest if states > 1 else float("inf")


def _is_well_formed(model: hmm.GaussianHMM) -> bool:
    """Whether a fitted model is a probability model at all.

    Checked here rather than trusted, because a fit can converge onto something that is not one. A
    state no transition was ever observed out of leaves its row of the transition matrix summing to
    zero and its mean NaN, and ``monitor_.converged`` still reports true -- the likelihood did stop
    moving. The runtime would refuse such an artifact; there is no reason to build one.
    """
    start = np.asarray(model.startprob_, dtype=np.float64)
    transitions = np.asarray(model.transmat_, dtype=np.float64)
    means = np.asarray(model.means_, dtype=np.float64)
    covariances = np.asarray(model._covars_, dtype=np.float64)

    if not (np.all(np.isfinite(start)) and np.all(np.isfinite(transitions))):
        return False
    if not (np.all(np.isfinite(means)) and np.all(np.isfinite(covariances))):
        return False
    if np.any(covariances <= 0.0):
        return False
    if abs(float(start.sum()) - 1.0) > 1e-6:
        return False
    return bool(np.all(np.abs(transitions.sum(axis=1) - 1.0) <= 1e-6))


def _canonical_order(model: hmm.GaussianHMM, ordering_feature: int) -> NDArray[np.intp]:
    """States sorted ascending by one feature's fitted mean, so labels survive a retrain."""
    return np.argsort(np.asarray(model.means_, dtype=np.float64)[:, ordering_feature], kind="stable")


def _reorder(model: hmm.GaussianHMM, order: NDArray[np.intp]) -> hmm.GaussianHMM:
    """The same model with its states relabelled -- an identical distribution, stable names.

    The transition matrix is permuted on both axes. Permuting only the rows leaves a matrix that is
    still row-stochastic and still a valid model, and is simply a different one; nothing about the
    result would look wrong.
    """
    reordered = hmm.GaussianHMM(
        n_components=int(model.n_components), covariance_type=str(model.covariance_type)
    )
    reordered.n_features = int(model.n_features)
    reordered.startprob_ = np.asarray(model.startprob_, dtype=np.float64)[order]
    reordered.transmat_ = np.asarray(model.transmat_, dtype=np.float64)[np.ix_(order, order)]
    reordered.means_ = np.asarray(model.means_, dtype=np.float64)[order]
    reordered._covars_ = np.asarray(model._covars_, dtype=np.float64)[order]
    return reordered


def fit_regime_hmm(
    observations: NDArray[np.float64],
    *,
    feature_names: list[str],
    n_states: int = 3,
    covariance_type: str = "diag",
    restarts: int = 8,
    n_iter: int = 500,
    seed: int = 0,
    ordering_feature: int = 0,
    state_labels: list[str] | None = None,
) -> RegimeHmmFit:
    """Fit from several initialisations, keep the best likelihood, and refuse a degenerate result.

    Baum-Welch is a local optimiser on a surface with many optima, so a single fit is a sample of
    the initialisation rather than an estimate of the model. Restarting and keeping the best
    likelihood is what ``hmmlearn``'s own model-selection guidance does, and it is the difference
    between a regime model and a random partition of the data.
    """
    if covariance_type not in SUPPORTED_COVARIANCE_TYPES:
        raise HmmFitRejected(f"covariance_type must be one of {sorted(SUPPORTED_COVARIANCE_TYPES)}")

    data = np.asarray(observations, dtype=np.float64)
    if data.ndim != 2:
        raise HmmFitRejected("observations must be two-dimensional")
    if data.shape[1] != len(feature_names):
        raise HmmFitRejected("observation width does not match the number of feature names")
    if not np.all(np.isfinite(data)):
        raise HmmFitRejected("observations contain non-finite values")
    if not 0 <= ordering_feature < data.shape[1]:
        raise HmmFitRejected("ordering_feature is outside the feature vector")

    best: hmm.GaussianHMM | None = None
    best_score = -np.inf
    for attempt in range(restarts):
        candidate = hmm.GaussianHMM(
            n_components=n_states,
            covariance_type=covariance_type,
            n_iter=n_iter,
            random_state=seed + attempt,
        )
        try:
            candidate.fit(data)
            if not candidate.monitor_.converged:
                continue
            if not _is_well_formed(candidate):
                continue
            score = float(candidate.score(data))
        except ValueError:
            # hmmlearn's own validation, raised from inside fit or score. Asking for more states
            # than the data supports leaves rows of the transition matrix summing to zero -- no
            # transition out of that state was ever observed -- and the means for it come back NaN.
            # The monitor still reports convergence, because the likelihood did stop moving.
            #
            # That is a restart which did not work, not a defect in this module, so it is skipped
            # like any other. Letting it escape would hand the caller a library exception it cannot
            # distinguish from a bug here.
            continue
        if score > best_score:
            best, best_score = candidate, score

    if best is None:
        raise HmmFitRejected(
            f"none of {restarts} initialisations produced a usable fit; the data likely does not "
            f"support {n_states} states"
        )

    ordered = _reorder(best, _canonical_order(best, ordering_feature))
    variances = np.asarray(np.diagonal(ordered.covars_, axis1=1, axis2=2), dtype=np.float64)
    if not np.all(variances > 0.0):
        raise HmmFitRejected("a fitted variance is not positive; its emission density is undefined")

    assignments = ordered.predict(data)
    occupancy = np.bincount(assignments, minlength=n_states).astype(np.float64) / len(data)
    if float(occupancy.min()) < MINIMUM_STATE_OCCUPANCY:
        raise HmmFitRejected(
            f"state occupancy {occupancy.round(4).tolist()} has a state below "
            f"{MINIMUM_STATE_OCCUPANCY}; it is an initialisation artefact, not a regime"
        )

    separation = _minimum_separation(np.asarray(ordered.means_, dtype=np.float64), variances)
    if separation < MINIMUM_STATE_SEPARATION:
        raise HmmFitRejected(
            f"the closest pair of states is {separation:.3f} pooled standard deviations apart, "
            f"below {MINIMUM_STATE_SEPARATION}; these are slices of one distribution, and a "
            "regime-change signal derived from them would be noise"
        )

    labels = state_labels or [f"state_{index}" for index in range(n_states)]
    if len(labels) != n_states:
        raise HmmFitRejected("state_labels does not match the number of states")

    return RegimeHmmFit(
        model=ordered,
        covariance_type=covariance_type,
        feature_names=list(feature_names),
        state_labels=labels,
        occupancy=occupancy,
        log_likelihood=float(ordered.score(data)),
        converged=True,
        restarts=restarts,
        observations=len(data),
        training_sequence=data,
    )


def _entropy(posterior: NDArray[np.float64]) -> float:
    """How undecided a posterior is. Zero when it has collapsed onto one state."""
    positive = posterior[posterior > 0.0]
    return float(-np.sum(positive * np.log(positive)))


def _corruptions(fit: RegimeHmmFit) -> list[tuple[str, hmm.GaussianHMM]]:
    """Models that are wrong in the ways a hand port actually gets them wrong.

    Used to prove the parity suite can fail. A suite drawn from saturated windows passes under a
    transposed transition matrix as readily as under the right one, and a check that cannot fail is
    not evidence -- it is the appearance of evidence, which is worse than none because it stops
    anyone looking.
    """
    transposed = _reorder(fit.model, np.arange(fit.n_states, dtype=np.intp))
    matrix = np.asarray(fit.model.transmat_, dtype=np.float64).T
    transposed.transmat_ = matrix / matrix.sum(axis=1, keepdims=True)

    swapped = _reorder(fit.model, np.arange(fit.n_states, dtype=np.intp))
    order = np.arange(fit.n_states, dtype=np.intp)
    order[0], order[-1] = order[-1], order[0]
    swapped.means_ = np.asarray(fit.model.means_, dtype=np.float64)[order]

    deviated = _reorder(fit.model, np.arange(fit.n_states, dtype=np.intp))
    # A covariance read where a standard deviation was meant: plausible densities, wrong model.
    deviated._covars_ = np.sqrt(np.asarray(fit.model._covars_, dtype=np.float64))

    return [("transposed_transitions", transposed), ("swapped_means", swapped),
            ("variance_as_deviation", deviated)]


def hmm_parity_cases(
    fit: RegimeHmmFit,
    *,
    sequence_length: int = 8,
    count: int = 3,
    tolerance: float = 1e-12,
) -> list[ParityCase]:
    """Sequences, with the filtered posterior ``hmmlearn`` gives for the end of each.

    Read the module docstring before changing this. ``predict_proba(sequence)`` row by row is the
    smoothed posterior and is not what the runtime computes; only the *last* row of a prefix is the
    filtered posterior. So each case runs the library once and keeps that last row.

    Two things decide which windows are chosen, and neither is convenience.

    Sequences are at least two observations long, because a one-observation case starts from
    ``startprob_`` and never touches the transition matrix -- which is how the previous parity check
    passed while leaving transitions entirely unverified.

    Windows are then ranked by the entropy of their final posterior, because a saturated posterior
    discriminates nothing. Drawn naively from the head of the training data, all three cases came
    back as [0, 1, 0]: correct, reproduced to 2e-16, and satisfied just as well by a transposed
    transition matrix. An undecided posterior is one the transition matrix actually shaped.
    """
    if sequence_length < 2:
        raise HmmFitRejected("a parity sequence shorter than two never exercises the transitions")
    if fit.observations < sequence_length * (count + 1):
        raise HmmFitRejected("not enough observations to draw distinct parity sequences")

    windows = [
        fit.training_sequence[start : start + sequence_length]
        for start in range(0, fit.observations - sequence_length, sequence_length)
    ]
    posteriors = [
        np.asarray(fit.model.predict_proba(window), dtype=np.float64)[-1] for window in windows
    ]
    ranked = sorted(range(len(windows)), key=lambda i: _entropy(posteriors[i]), reverse=True)[:count]

    cases: list[ParityCase] = []
    for index in ranked:
        window = windows[index]
        # The independent anchor: the forward normalisers sum to the sequence log-likelihood the
        # library reports, so a filter that gets the posteriors right and this wrong has the
        # recursion right and the normalisation wrong.
        if not np.isfinite(float(fit.model.score(window))):
            raise HmmFitRejected("a parity sequence has no finite likelihood under the fit")
        cases.append(
            ParityCase(
                inputs=[[float(value) for value in row] for row in window],
                expected=[float(value) for value in posteriors[index]],
            )
        )

    _require_discrimination(fit, cases, tolerance)
    return cases


def _require_discrimination(
    fit: RegimeHmmFit, cases: list[ParityCase], tolerance: float
) -> None:
    """Refuse a parity suite that a wrong model would also satisfy."""
    for name, corrupted in _corruptions(fit):
        separated = False
        for case in cases:
            window = np.asarray(case.inputs, dtype=np.float64)
            wrong = np.asarray(corrupted.predict_proba(window), dtype=np.float64)[-1]
            if float(np.abs(wrong - np.asarray(case.expected)).max()) > tolerance:
                separated = True
                break
        if not separated:
            raise HmmFitRejected(
                f"the parity cases cannot distinguish the fit from a {name} copy of it; "
                "they would pass against a wrong model and prove nothing"
            )


def export_regime_hmm_artifact(
    fit: RegimeHmmFit,
    *,
    artifact_id: str,
    model_id: str,
    model_version: str,
    dataset_hash: str,
    git_commit: str,
    random_seed: int,
    as_of: datetime,
    bar_duration_minutes: int,
    support_domain: SupportDomain,
    lookback_periods: int,
    feature_units: dict[str, str],
    evidence_grade: str = "B",
    promotion_state: str = "VALIDATED",
) -> RuntimeInferenceArtifact:
    """Seal the fit into the artifact the runtime loads, addressing every value by name."""
    schema = feature_schema_of(
        schema_version=f"gaussian-hmm-{fit.covariance_type}-v1",
        feature_names=fit.feature_names,
        dtypes=dict.fromkeys(fit.feature_names, "float64"),
        normalization={},
        lookback_periods=lookback_periods,
        source_requirements=["alpaca_ohlcv"],
    )

    # Every value keyed by name rather than written in array order. A flat map read positionally
    # would depend on dictionary ordering, which is the class of failure the schema hash exists to
    # stop, and a transposed transition matrix still yields a valid probability vector.
    parameters: dict[str, float] = {
        "n_states": float(fit.n_states),
        "n_features": float(fit.n_features),
    }
    start = np.asarray(fit.model.startprob_, dtype=np.float64)
    transitions = np.asarray(fit.model.transmat_, dtype=np.float64)
    means = np.asarray(fit.model.means_, dtype=np.float64)
    variances = fit.variances()
    for i in range(fit.n_states):
        parameters[f"start_{i}"] = float(start[i])
        for j in range(fit.n_states):
            parameters[f"trans_{i}_{j}"] = float(transitions[i, j])
        for f in range(fit.n_features):
            parameters[f"mean_{i}_{f}"] = float(means[i, f])
            parameters[f"var_{i}_{f}"] = float(variances[i, f])

    artifact = RuntimeInferenceArtifact(
        artifact_id=artifact_id,
        model_id=model_id,
        model_family="hmm",
        model_version=model_version,
        producer=ProducerIdentity(
            library="hmmlearn",
            library_version=hmmlearn.__version__,
            numpy_version=np.__version__,
        ),
        feature_schema=schema,
        feature_schema_hash=schema.feature_hash,
        support_domain=support_domain,
        feature_semantics=FeatureSemantics(
            units=feature_units,
            missing_policy="refuse",
            lookback_periods=lookback_periods,
            bar_duration_minutes=bar_duration_minutes,
        ),
        dataset_hash=dataset_hash,
        parameters=parameters,
        variant={
            "covariance_type": fit.covariance_type,
            "emission": "gaussian",
            "inference": "forward_filter",
            "state_labels": ",".join(fit.state_labels),
        },
        payload={"state_labels": fit.state_labels},
        random_seed=random_seed,
        evidence_grade=evidence_grade,
        promotion_state=promotion_state,
        diagnostics={
            "converged": fit.converged,
            "restarts": fit.restarts,
            "log_likelihood": fit.log_likelihood,
            "observations": fit.observations,
            "state_occupancy": [float(value) for value in fit.occupancy],
            "minimum_variance": float(fit.variances().min()),
        },
        git_commit=git_commit,
        created_at=utc_now(),
        as_of=as_of,
        parity=ParitySuite(
            kind="sequence_to_vector",
            # Probabilities are bounded by one, so an absolute bound is the meaningful one; a
            # relative bound on a posterior of 1e-16 would demand precision the arithmetic in
            # either language cannot deliver.
            absolute_tolerance=1e-12,
            relative_tolerance=0.0,
            cases=hmm_parity_cases(fit),
        ),
    )
    return artifact.sealed()
