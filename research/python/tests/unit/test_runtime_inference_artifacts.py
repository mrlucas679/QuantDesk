"""The models that cross into C#, fitted by their real libraries and checked against them.

What these tests are for
------------------------
The C# side already refuses a model it cannot reproduce. What it could not do was verify anything,
because the parity vectors in its own tests were computed by the C# implementation itself -- a check
of the code against its own arithmetic, which passes just as happily when the arithmetic is wrong.

So the rule these tests enforce is that every expected answer comes from ``arch``, ``hmmlearn`` or
``lightgbm``, and the replays below score the same inputs the way the runtime will: from the
exported parameters alone, with no access to the fitted object.
"""

from __future__ import annotations

import json
import math
from datetime import UTC, datetime
from pathlib import Path

import lightgbm as lgb
import numpy as np
import pytest
from numpy.typing import NDArray

from quantdesk_research.models.fixture_generation import FIXTURE_NAMES, build_all
from quantdesk_research.models.garch import (
    GarchFitRejected,
    export_garch_artifact,
    fit_garch,
    required_warmup_bars,
)
from quantdesk_research.models.regime_hmm import (
    HmmFitRejected,
    export_regime_hmm_artifact,
    fit_regime_hmm,
)
from quantdesk_research.models.runtime_artifact import RuntimeInferenceArtifact, SupportDomain
from quantdesk_research.models.tree_export import (
    TreeExportRejected,
    _score,
    export_tree_artifact,
    flatten_booster,
    held_out_performance,
    route,
    threshold_probes,
)

AS_OF = datetime(2026, 9, 3, tzinfo=UTC)


# ---------------------------------------------------------------------------- GARCH


def _garch_returns(n: int = 4000) -> NDArray[np.float64]:
    rng = np.random.default_rng(3)
    returns = np.zeros(n)
    variance = np.zeros(n)
    variance[0] = 1e-4
    for t in range(1, n):
        variance[t] = 2e-6 + 0.08 * returns[t - 1] ** 2 + 0.90 * variance[t - 1]
        returns[t] = rng.normal(0.0, math.sqrt(variance[t]))
    return returns * 100.0


def test_a_cold_recursion_lands_on_the_path_arch_fitted() -> None:
    """The claim that makes carrying state unnecessary.

    Each parity case is a warm-up window and the conditional variance ``arch`` reports at its end.
    The replay here starts from zero -- deliberately the wrong seed, and nothing like arch's 0.94
    backcast -- and still arrives at the same number, because the seed enters multiplied by beta
    once per bar and the window is sized from beta so that it cannot survive.
    """
    fit = fit_garch(_garch_returns(), return_units="percent")
    artifact = export_garch_artifact(
        fit,
        artifact_id="garch-test",
        model_id="crypto-conditional-variance",
        model_version="1.0.0",
        dataset_hash="dataset-abc",
        git_commit="abc1234",
        random_seed=1,
        as_of=AS_OF,
        bar_duration_minutes=5,
        support_domain=SupportDomain(asset_class="spot_crypto", symbols=["BTC/USD"], bar_duration_minutes=5),
    )

    omega = artifact.parameters["omega"]
    alpha = artifact.parameters["alpha"]
    beta = artifact.parameters["beta"]

    for case in artifact.parity.cases:
        variance = 0.0
        for observation in case.inputs:
            variance = omega + alpha * observation[0] + beta * variance
        assert variance == pytest.approx(case.expected[0], rel=1e-9, abs=1e-18)


def test_the_warmup_window_is_derived_from_persistence_not_chosen() -> None:
    """A round number picked by taste is wasteful on a fast fit and wrong on a persistent one."""
    assert required_warmup_bars(0.5) < required_warmup_bars(0.9) < required_warmup_bars(0.99)
    assert 0.9 ** required_warmup_bars(0.9) <= 1e-16

    with pytest.raises(GarchFitRejected):
        required_warmup_bars(1.0)


def test_units_must_be_declared_because_omega_does_not_carry_them() -> None:
    """A model fitted on percent returns cannot consume decimal ones just because omega survived."""
    with pytest.raises(GarchFitRejected):
        fit_garch(_garch_returns(), return_units="whatever_the_caller_had")


def test_a_short_history_is_refused_rather_than_fitted() -> None:
    with pytest.raises(GarchFitRejected):
        fit_garch(_garch_returns(200), return_units="percent")


def test_the_artifact_records_which_arch_produced_it() -> None:
    fit = fit_garch(_garch_returns(), return_units="percent")
    artifact = export_garch_artifact(
        fit,
        artifact_id="garch-test",
        model_id="m",
        model_version="1.0.0",
        dataset_hash="d",
        git_commit="c",
        random_seed=1,
        as_of=AS_OF,
        bar_duration_minutes=5,
        support_domain=SupportDomain(asset_class="spot_crypto", symbols=["BTC/USD"], bar_duration_minutes=5),
    )
    assert artifact.producer.library == "arch"
    assert artifact.producer.library_version
    assert artifact.variant["mean"] == "Zero"
    assert artifact.variant["horizon"] == "one_step"
    assert artifact.diagnostics["persistence"] < 1.0


# ---------------------------------------------------------------------------- HMM


def _regime_observations() -> NDArray[np.float64]:
    rng = np.random.default_rng(21)
    return np.vstack(
        [
            rng.normal([0.0, 1.0], [0.4, 0.3], (400, 2)),
            rng.normal([2.0, 3.0], [0.5, 0.4], (400, 2)),
            rng.normal([-2.0, 0.2], [0.3, 0.2], (400, 2)),
        ]
    )


def _fitted_hmm():
    return fit_regime_hmm(
        _regime_observations(),
        feature_names=["vol_pct", "adx_norm"],
        n_states=3,
        state_labels=["calm", "normal", "stress"],
    )


def _hmm_artifact() -> RuntimeInferenceArtifact:
    return export_regime_hmm_artifact(
        _fitted_hmm(),
        artifact_id="hmm-test",
        model_id="crypto-regime",
        model_version="1.0.0",
        dataset_hash="dataset-abc",
        git_commit="abc1234",
        random_seed=0,
        as_of=AS_OF,
        bar_duration_minutes=5,
        support_domain=SupportDomain(asset_class="spot_crypto", symbols=["BTC/USD"], bar_duration_minutes=5),
        lookback_periods=360,
        feature_units={"vol_pct": "percentile", "adx_norm": "ratio"},
    )


def _forward_filter(parameters: dict[str, float], sequence: list[list[float]]) -> NDArray[np.float64]:
    """The runtime's filter, from the named parameters only, in log space.

    In logs because multiplying densities underflows to zero for even a handful of features, which
    turns the posterior into a division of zero by zero -- silently, and only on the quiet days when
    densities are small.
    """
    states = int(parameters["n_states"])
    features = int(parameters["n_features"])

    def log_sum_exp(values: NDArray[np.float64]) -> float:
        largest = float(values.max())
        if not math.isfinite(largest):
            return largest
        return largest + float(np.log(np.exp(values - largest).sum()))

    def log_or_negative_infinity(value: float) -> float:
        # A zero probability is zero, not the smallest representable double. Flooring it makes an
        # impossible transition merely unlikely, and on a quiet bar an unlikely-but-possible state
        # can outweigh a genuinely reachable one.
        return math.log(value) if value > 0.0 else -math.inf

    posterior: NDArray[np.float64] | None = None
    for observation in sequence:
        if posterior is None:
            prior = np.array(
                [log_or_negative_infinity(parameters[f"start_{i}"]) for i in range(states)]
            )
        else:
            prior = np.array(
                [
                    log_sum_exp(
                        np.array(
                            [
                                posterior[i]
                                + log_or_negative_infinity(parameters[f"trans_{i}_{j}"])
                                for i in range(states)
                            ]
                        )
                    )
                    for j in range(states)
                ]
            )
        emission = np.array(
            [
                sum(
                    -0.5
                    * (
                        math.log(2.0 * math.pi * parameters[f"var_{i}_{f}"])
                        + (observation[f] - parameters[f"mean_{i}_{f}"]) ** 2
                        / parameters[f"var_{i}_{f}"]
                    )
                    for f in range(features)
                )
                for i in range(states)
            ]
        )
        combined = prior + emission
        posterior = combined - log_sum_exp(combined)

    assert posterior is not None
    return np.exp(posterior)


def test_the_forward_filter_reproduces_what_hmmlearn_filtered() -> None:
    artifact = _hmm_artifact()
    for case in artifact.parity.cases:
        replayed = _forward_filter(artifact.parameters, case.inputs)
        assert np.abs(replayed - np.asarray(case.expected)).max() <= artifact.parity.absolute_tolerance


def test_parity_sequences_are_long_enough_to_touch_the_transition_matrix() -> None:
    """A one-observation case starts from startprob_ and never exercises transitions at all.

    That is exactly how the earlier parity check passed while leaving the transition matrix
    completely unverified.
    """
    for case in _hmm_artifact().parity.cases:
        assert len(case.inputs) >= 2


def test_states_are_ordered_so_a_retrain_cannot_permute_the_regimes() -> None:
    """State 0 today is not inherently the same regime as state 0 tomorrow.

    Left alone, a retrain that merely renumbers the states makes every regime-change interrupt fire
    on a change that did not happen.
    """
    artifact = _hmm_artifact()
    states = int(artifact.parameters["n_states"])
    means = [artifact.parameters[f"mean_{i}_0"] for i in range(states)]
    assert means == sorted(means)
    assert artifact.payload["state_labels"] == ["calm", "normal", "stress"]


def test_variances_are_exported_per_feature_not_as_expanded_matrices() -> None:
    """``covars_`` expands a diagonal fit to (states, features, features) via ``fill_covars``.

    Reading whatever shape appears and calling it the wire format exports a matrix where a vector
    was meant, and nothing about the resulting numbers looks wrong.
    """
    artifact = _hmm_artifact()
    states = int(artifact.parameters["n_states"])
    features = int(artifact.parameters["n_features"])
    for i in range(states):
        for f in range(features):
            assert artifact.parameters[f"var_{i}_{f}"] > 0.0
    assert f"var_{states - 1}_{features}" not in artifact.parameters


def test_states_that_are_slices_of_one_distribution_are_refused() -> None:
    """Occupancy does not catch an over-specified model, which took measuring to notice.

    Six states fitted to a single Gaussian blob converge, validate, and come back with occupancy
    near one sixth each. Nothing is degenerate: the model has carved one distribution into six
    arbitrary slices and estimated every slice well. Whether the states are *distinguishable* is a
    different question from whether they are populated, and it is the one that decides whether a
    regime-change signal means anything.
    """
    rng = np.random.default_rng(5)
    single = rng.normal([0.0, 0.0], [0.1, 0.1], (600, 2))
    with pytest.raises(HmmFitRejected, match="pooled standard deviations"):
        fit_regime_hmm(single, feature_names=["a", "b"], n_states=6, restarts=3)


def test_a_state_almost_nothing_visits_is_refused() -> None:
    """A regime estimated from a handful of bars is an initialisation artefact."""
    rng = np.random.default_rng(6)
    lopsided = np.vstack(
        [
            rng.normal([0.0, 0.0], [0.3, 0.3], (598, 2)),
            rng.normal([40.0, 40.0], [0.3, 0.3], (2, 2)),
        ]
    )
    with pytest.raises(HmmFitRejected, match="occupancy"):
        fit_regime_hmm(lopsided, feature_names=["a", "b"], n_states=2, restarts=3)


def test_the_covariance_type_must_be_one_the_runtime_reproduces() -> None:
    with pytest.raises(HmmFitRejected):
        fit_regime_hmm(
            _regime_observations(), feature_names=["vol_pct", "adx_norm"], covariance_type="full"
        )


# ---------------------------------------------------------------------------- LightGBM


def _booster(params: dict[str, object], data: NDArray[np.float64]) -> lgb.Booster:
    rng = np.random.default_rng(4)
    target = (
        np.nan_to_num(data[:, 0]) * 2.0 - np.nan_to_num(data[:, 1]) + rng.normal(scale=0.1, size=len(data))
    )
    return lgb.train(
        {
            "objective": "regression",
            "verbosity": -1,
            "num_leaves": 8,
            "seed": 3,
            "deterministic": True,
            "learning_rate": 0.2,
            **params,
        },
        lgb.Dataset(data, label=target),
        num_boost_round=5,
    )


def _dense() -> NDArray[np.float64]:
    return np.random.default_rng(11).normal(size=(600, 4))


def _with_zeros() -> NDArray[np.float64]:
    rng = np.random.default_rng(12)
    return np.where(rng.random((600, 4)) < 0.2, 0.0, rng.normal(size=(600, 4)))


def _with_nans() -> NDArray[np.float64]:
    rng = np.random.default_rng(13)
    return np.where(rng.random((600, 4)) < 0.2, np.nan, rng.normal(size=(600, 4)))


@pytest.mark.parametrize(
    ("params", "data", "expected_missing_type"),
    [
        ({}, _dense(), "None"),
        ({"zero_as_missing": True}, _with_zeros(), "Zero"),
        ({"use_missing": True}, _with_nans(), "NaN"),
    ],
)
def test_the_traversal_reproduces_the_booster_on_every_missing_convention(
    params: dict[str, object], data: NDArray[np.float64], expected_missing_type: str
) -> None:
    """The bug this exists to catch: every non-finite feature down the default branch.

    LightGBM decides by the node's ``missing_type``, and only under ``"NaN"`` does a NaN take the
    default branch. Under ``"None"`` it becomes zero and meets the threshold like any other value;
    under ``"Zero"`` an ordinary 0.0 is the missing one. Measured on a real booster, the wrong rule
    scored 2.369 where the booster scored 5.070.
    """
    booster = _booster(params, data)
    export = flatten_booster(booster)

    observed = {
        node["missing_type"] for tree in export.trees for node in tree.nodes if node["split_feature"] >= 0
    }
    assert observed == {expected_missing_type}

    probes = threshold_probes(export, np.nan_to_num(data[0]))
    scored = np.array([_score(export.trees, row) for row in probes])
    predicted = booster.predict(probes, raw_score=True, num_iteration=export.num_iteration)
    assert np.abs(scored - np.asarray(predicted)).max() == 0.0


def test_the_zero_bound_is_the_float_literal_not_the_double() -> None:
    """LightGBM's ``kZeroThreshold`` is ``1e-35f``, which widens to 1.0000000180025095e-35.

    The gap between that and the double 1e-35 is not hypothetical: a fitted booster split at
    -1.0000000180025095e-35 and routed it as missing, while a double bound called it an ordinary
    value and sent it down the other branch. One probe in 121 disagreed.
    """
    node = {"missing_type": "Zero", "default_left": True, "threshold": -1.0000000180025095e-35}
    assert route(node, -1.0000000180025095e-35) is True  # missing, so the default branch
    assert route(node, float(np.nextafter(-1.0000000180025095e-35, -np.inf))) is True  # <= threshold


def test_probes_include_the_thresholds_themselves() -> None:
    """A traversal resolving ``<=`` as ``<`` differs only on inputs sitting exactly on a threshold.

    No randomly drawn probe ever does, which is why the thresholds are read out of the trees and fed
    back in.
    """
    export = flatten_booster(_booster({}, _dense()))
    probes = threshold_probes(export, np.nan_to_num(_dense()[0]))
    thresholds = {
        node["threshold"] for tree in export.trees for node in tree.nodes if node["split_feature"] >= 0
    }
    present = {float(value) for row in probes for value in row}
    assert thresholds <= present


def test_the_trees_are_inside_the_artifact_and_inside_its_hash() -> None:
    """An ensemble whose scoring data arrives out of band hashes everything but the answer."""
    data = _dense()
    booster = _booster({}, data[:500])
    held_out = data[500:]
    artifact = export_tree_artifact(
        booster,
        probes=np.nan_to_num(data[:1]),
        artifact_id="lgb-test",
        model_id="crypto-direction",
        model_version="1.0.0",
        dataset_hash="dataset-abc",
        git_commit="abc1234",
        random_seed=3,
        as_of=AS_OF,
        bar_duration_minutes=5,
        support_domain=SupportDomain(asset_class="spot_crypto", symbols=["BTC/USD"], bar_duration_minutes=5),
        lookback_periods=48,
        feature_units=dict.fromkeys(booster.feature_name(), "zscore"),
        target_units="basis_points",
        held_out_features=held_out,
        held_out_target=np.nan_to_num(held_out[:, 0]) * 2.0 - np.nan_to_num(held_out[:, 1]),
        training_mean=0.0,
    )

    assert artifact.hash_matches()
    assert len(artifact.payload["trees"]) == len(flatten_booster(booster).trees)

    tampered_trees = [list(tree) for tree in artifact.payload["trees"]]
    tampered_trees[0][0] = {**tampered_trees[0][0], "threshold": 99.0}
    assert not artifact.model_copy(
        update={"payload": {"trees": tampered_trees}}
    ).hash_matches()


def test_an_objective_carrying_a_link_is_refused() -> None:
    """The sum of leaves is on the link's scale, so a binary model yields log-odds read as a return."""
    rng = np.random.default_rng(9)
    data = rng.normal(size=(400, 3))
    booster = lgb.train(
        {"objective": "binary", "verbosity": -1, "num_leaves": 4, "seed": 1, "deterministic": True},
        lgb.Dataset(data, label=(data[:, 0] > 0).astype(float)),
        num_boost_round=3,
    )
    with pytest.raises(TreeExportRejected):
        flatten_booster(booster)


def test_random_forest_mode_is_refused_because_it_averages() -> None:
    rng = np.random.default_rng(10)
    data = rng.normal(size=(500, 3))
    booster = lgb.train(
        {
            "objective": "regression",
            "boosting": "rf",
            "bagging_freq": 1,
            "bagging_fraction": 0.8,
            "feature_fraction": 0.8,
            "verbosity": -1,
            "num_leaves": 4,
            "seed": 1,
            "deterministic": True,
        },
        lgb.Dataset(data, label=data[:, 0]),
        num_boost_round=4,
    )
    with pytest.raises(TreeExportRejected):
        flatten_booster(booster)



def test_a_fit_that_does_not_clear_the_required_skill_is_refused() -> None:
    """Parity proves C# reproduces the fit. It says nothing about whether the fit is worth it.

    An artifact that crossed the boundary perfectly while having learned nothing would be scored by
    the runtime forever with no one the wiser, so a held-out set is required rather than optional.

    The bar is raised above what this booster achieves rather than fitting a deliberately useless
    model, because a model fitted on noise lands either side of zero skill by luck and a test that
    depends on which side it landed is a test that fails on somebody else's machine.
    """
    data = _dense()
    booster = _booster({}, data[:500])
    held_out = data[500:]

    with pytest.raises(TreeExportRejected, match="does not beat saying nothing"):
        export_tree_artifact(
            booster,
            probes=np.nan_to_num(data[:1]),
            artifact_id="lgb-test",
            model_id="crypto-direction",
            model_version="1.0.0",
            dataset_hash="dataset-abc",
            git_commit="abc1234",
            random_seed=3,
            as_of=AS_OF,
            bar_duration_minutes=5,
            support_domain=SupportDomain(asset_class="spot_crypto", symbols=["BTC/USD"], bar_duration_minutes=5),
            lookback_periods=48,
            feature_units=dict.fromkeys(booster.feature_name(), "zscore"),
            target_units="basis_points",
            held_out_features=held_out,
            held_out_target=np.nan_to_num(held_out[:, 0]) * 2.0 - np.nan_to_num(held_out[:, 1]),
            training_mean=0.0,
            minimum_skill=0.999,
        )


def test_skill_is_measured_against_the_training_mean_not_against_zero() -> None:
    """A model that only reproduces the sample mean has learned where the data sits, not what
    moves it -- and against a zero baseline that reads as skill."""
    export = flatten_booster(_booster({}, _dense()[:500]))
    booster = _booster({}, _dense()[:500])
    held_out = _dense()[500:]
    # Offset from zero, with noise, so the mean baseline is a real bar rather than a perfect one.
    # A constant target would make the mean baseline exactly right and its error zero, which is a
    # degenerate case rather than the point.
    target = 5.0 + np.random.default_rng(41).normal(scale=0.5, size=len(held_out))

    against_mean = held_out_performance(booster, export, held_out, target, training_mean=5.0)
    against_zero = held_out_performance(booster, export, held_out, target, training_mean=0.0)

    assert against_mean.baseline == "training_mean"
    assert against_mean.baseline_rmse < against_zero.baseline_rmse
    assert against_mean.skill < against_zero.skill


def test_the_committed_tree_fixture_reports_its_held_out_skill() -> None:
    """The number sits in the artifact, not in a report nobody reads.

    It is a floor, not evidence of alpha: a model can remove a third of a return series' variance
    and still lose on every round trip once the venue charges for one, which is what this system's
    own measurements say happens.
    """
    committed = RuntimeInferenceArtifact.model_validate_json(
        (_fixture_root() / FIXTURE_NAMES["lightgbm"]).read_text(encoding="utf-8")
    )
    assert committed.diagnostics["held_out_observations"] > 0
    assert committed.diagnostics["held_out_skill"] > 0.5
    assert committed.diagnostics["held_out_baseline"] == "training_mean"
    assert committed.diagnostics["held_out_rmse"] < committed.diagnostics["held_out_baseline_rmse"]


# ---------------------------------------------------------------------------- the contract


def test_an_artifact_with_no_parity_cases_cannot_be_built() -> None:
    """A model nobody can verify is the thing this whole contract exists to prevent."""
    from quantdesk_research.models.runtime_artifact import ParitySuite

    with pytest.raises(ValueError):
        ParitySuite(kind="vector_to_scalar", absolute_tolerance=0.0, relative_tolerance=0.0, cases=[])


def test_sealing_covers_the_variant_flags_too() -> None:
    """Variant flags decide which inference path runs; a hash that skipped them would let a
    categorical model arrive labelled as a numeric one without changing the identity."""
    artifact = _hmm_artifact()
    assert artifact.hash_matches()
    assert not artifact.model_copy(
        update={"variant": {**artifact.variant, "covariance_type": "full"}}
    ).hash_matches()


# ---------------------------------------------------------------------------- committed fixtures


def _fixture_root() -> Path:
    directory = Path(__file__).resolve()
    while directory.parent != directory and not (directory / ".git").exists():
        directory = directory.parent
    return directory / "tests" / "fixtures" / "model-artifacts"


def test_no_committed_fixture_contains_a_json_token_dotnet_cannot_parse() -> None:
    """NaN and Infinity are not JSON, however readily Python writes and reads them.

    The first generated LightGBM fixture carried a bare ``NaN``, because its probe grid puts a
    missing value into every feature on purpose. .NET's parser rejects that token, so the file the
    runtime was meant to load was a file it could not open. Missing is ``null`` now.
    """
    # parse_constant fires only on the bare literals, so "missing_type": "NaN" -- a perfectly
    # ordinary string naming LightGBM's convention -- is left alone. A substring search would flag
    # it and teach the next reader to ignore this test.
    for name in FIXTURE_NAMES.values():
        text = (_fixture_root() / name).read_text(encoding="utf-8")
        json.loads(text, parse_constant=_refuse_constant)


def _refuse_constant(token: str) -> float:
    raise AssertionError(f"fixture contains the non-JSON token {token!r}")


@pytest.mark.parametrize("family", sorted(FIXTURE_NAMES))
def test_the_committed_fixtures_regenerate_byte_for_byte(family: str) -> None:
    """The fixtures C# loads must be reproducible from this code, not merely present.

    Every seed is fixed and ``created_at`` is pinned, so a change to any exporter shows up here as
    a differing artifact hash. Without this the fixtures would drift from the code that claims to
    produce them, and the runtime would keep passing against a file nobody could rebuild.
    """
    committed = json.loads((_fixture_root() / FIXTURE_NAMES[family]).read_text(encoding="utf-8"))
    regenerated = build_all()[family].model_dump(mode="json")
    assert regenerated["artifact_hash"] == committed["artifact_hash"]
    assert regenerated == committed


@pytest.mark.parametrize("family", sorted(FIXTURE_NAMES))
def test_every_committed_fixture_is_sealed_over_its_own_contents(family: str) -> None:
    committed = RuntimeInferenceArtifact.model_validate_json(
        (_fixture_root() / FIXTURE_NAMES[family]).read_text(encoding="utf-8")
    )
    assert committed.hash_matches()
    assert committed.parity.cases
    assert committed.producer.library_version


def test_an_artifact_survives_the_round_trip_through_disk_unchanged(tmp_path: Path) -> None:
    """The other half of the restart proof, on this side of the boundary.

    C# demonstrates that reloading a file reproduces the forecast. That is only worth anything if
    the file itself is a faithful record of the fit, so this writes one, reads it back as a fresh
    object with no memory of the fitting process, and requires every field and the seal to survive.

    JSON is where a double quietly loses its last bits, and a parity tolerance of 1e-12 would not
    notice. The hash does: it is computed over the serialised form, so a value that did not
    round-trip exactly changes it.
    """
    for family, name in FIXTURE_NAMES.items():
        original = build_all()[family]
        written = original.write(tmp_path / name)
        restored = RuntimeInferenceArtifact.model_validate_json(
            written.read_text(encoding="utf-8")
        )

        assert restored.hash_matches()
        assert restored.artifact_hash == original.artifact_hash
        assert restored.parameters == original.parameters
        assert restored.payload == original.payload
        for restored_case, original_case in zip(
            restored.parity.cases, original.parity.cases, strict=True
        ):
            assert restored_case.inputs == original_case.inputs
            assert restored_case.expected == original_case.expected


def test_the_lightgbm_fixture_exercises_a_missing_convention_the_runtime_got_wrong() -> None:
    """A fixture that only covered the rule the code already had would have shipped the bug."""
    committed = RuntimeInferenceArtifact.model_validate_json(
        (_fixture_root() / FIXTURE_NAMES["lightgbm"]).read_text(encoding="utf-8")
    )
    conventions = {
        node["missing_type"]
        for tree in committed.payload["trees"]
        for node in tree
        if node["split_feature"] >= 0
    }
    assert conventions == {"NaN"}
    assert any(
        any(value is None for value in case.inputs[0]) for case in committed.parity.cases
    ), "no probe carries a missing feature, so the missing-value branch is never taken"
