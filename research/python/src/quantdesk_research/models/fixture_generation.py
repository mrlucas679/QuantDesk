"""Generates the model artifacts the C# runtime tests load.

Why these are generated rather than hand-written
------------------------------------------------
The point of the bridge is that C# reproduces what ``arch``, ``hmmlearn`` and ``lightgbm``
computed. A fixture written by hand, or one whose expected answers were produced by the C#
implementation, tests the runtime against somebody's belief about the library rather than against
the library. The previous parity vectors were exactly that, and they passed.

So these files are fitted here, scored by the libraries themselves, and committed. C# loads the
same bytes and must arrive at the same numbers with no access to Python at all.

Determinism
-----------
Every seed is fixed and ``created_at`` is pinned, so regenerating produces byte-identical files and
an unchanged artifact hash. ``test_model_fixtures_are_reproducible`` regenerates and compares: if an
exporter changes, that test fails until the fixtures are regenerated, and the change then reaches
the C# side rather than sitting in Python unnoticed.
"""

from __future__ import annotations

import math
import warnings
from datetime import UTC, datetime
from pathlib import Path

import lightgbm as lgb
import numpy as np
from numpy.typing import NDArray

from quantdesk_research.models.garch import export_garch_artifact, fit_garch
from quantdesk_research.models.har import HARModel, export_har_artifact
from quantdesk_research.models.regime_hmm import export_regime_hmm_artifact, fit_regime_hmm
from quantdesk_research.models.runtime_artifact import RuntimeInferenceArtifact
from quantdesk_research.models.tree_export import export_tree_artifact

#: Pinned so regeneration is byte-identical and the artifact hash is stable.
FIXTURE_TIMESTAMP = datetime(2026, 9, 3, 0, 0, 0, tzinfo=UTC)

FIXTURE_COMMIT = "fixture-generated"

FIXTURE_NAMES = {
    "har": "har-realised-variance.json",
    "garch": "garch-conditional-variance.json",
    "hmm": "gaussian-hmm-regime.json",
    "lightgbm": "lightgbm-direction.json",
}


def _pinned(artifact: RuntimeInferenceArtifact) -> RuntimeInferenceArtifact:
    return artifact.model_copy(update={"created_at": FIXTURE_TIMESTAMP}).sealed()


def _garch_returns(n: int = 4000) -> NDArray[np.float64]:
    """A series with known volatility clustering, in percent."""
    rng = np.random.default_rng(3)
    returns = np.zeros(n)
    variance = np.zeros(n)
    variance[0] = 1e-4
    for t in range(1, n):
        variance[t] = 2e-6 + 0.08 * returns[t - 1] ** 2 + 0.90 * variance[t - 1]
        returns[t] = rng.normal(0.0, math.sqrt(variance[t]))
    return returns * 100.0


def _regime_observations() -> NDArray[np.float64]:
    """Three separated clusters, so the fitted states are regimes rather than arbitrary slices."""
    rng = np.random.default_rng(21)
    return np.vstack(
        [
            rng.normal([0.0, 1.0], [0.4, 0.3], (400, 2)),
            rng.normal([2.0, 3.0], [0.5, 0.4], (400, 2)),
            rng.normal([-2.0, 0.2], [0.3, 0.2], (400, 2)),
        ]
    )


def build_har() -> RuntimeInferenceArtifact:
    model = HARModel()
    model.fit(np.linspace(0.01, 0.05, 120))
    return _pinned(
        export_har_artifact(
            model,
            probes=[(0.04, 0.035, 0.03), (0.01, 0.01, 0.01), (0.05, 0.02, 0.045), (0.0, 0.0, 0.0)],
            artifact_id="har-realised-variance-fixture",
            model_id="crypto-realised-variance",
            model_version="1.0.0",
            dataset_hash="fixture-har-dataset",
            git_commit=FIXTURE_COMMIT,
            random_seed=0,
            as_of=FIXTURE_TIMESTAMP,
            bar_duration_minutes=5,
            short_bars=1,
            medium_bars=5,
            long_bars=22,
            variance_units="decimal_return_variance",
        )
    )


def build_garch() -> RuntimeInferenceArtifact:
    fit = fit_garch(_garch_returns(), return_units="percent")
    return _pinned(
        export_garch_artifact(
            fit,
            artifact_id="garch-conditional-variance-fixture",
            model_id="crypto-conditional-variance",
            model_version="1.0.0",
            dataset_hash="fixture-garch-dataset",
            git_commit=FIXTURE_COMMIT,
            random_seed=0,
            as_of=FIXTURE_TIMESTAMP,
            bar_duration_minutes=5,
        )
    )


def build_hmm() -> RuntimeInferenceArtifact:
    fit = fit_regime_hmm(
        _regime_observations(),
        feature_names=["vol_percentile", "adx_normalised"],
        n_states=3,
        state_labels=["calm", "normal", "stress"],
    )
    return _pinned(
        export_regime_hmm_artifact(
            fit,
            artifact_id="gaussian-hmm-regime-fixture",
            model_id="crypto-regime",
            model_version="1.0.0",
            dataset_hash="fixture-hmm-dataset",
            git_commit=FIXTURE_COMMIT,
            random_seed=0,
            as_of=FIXTURE_TIMESTAMP,
            bar_duration_minutes=5,
            lookback_periods=360,
            feature_units={"vol_percentile": "percentile", "adx_normalised": "ratio"},
        )
    )


def build_lightgbm() -> RuntimeInferenceArtifact:
    """A booster fitted with NaNs present, so its nodes carry the ``NaN`` missing convention.

    Deliberately the convention the first C# traversal happened to implement, paired in the same
    suite with the ``None`` convention it got wrong -- a fixture that only ever exercised the rule
    the code already had would have shipped the bug.
    """
    rng = np.random.default_rng(13)
    features = np.where(rng.random((800, 4)) < 0.2, np.nan, rng.normal(size=(800, 4)))
    target = (
        np.nan_to_num(features[:, 0]) * 2.0
        - np.nan_to_num(features[:, 1])
        + rng.normal(scale=0.1, size=800)
    )
    with warnings.catch_warnings():
        warnings.simplefilter("ignore")
        booster = lgb.train(
            {
                "objective": "regression",
                "verbosity": -1,
                "num_leaves": 8,
                "seed": 3,
                "deterministic": True,
                "learning_rate": 0.2,
                "use_missing": True,
                "num_threads": 1,
            },
            lgb.Dataset(features, label=target),
            num_boost_round=5,
        )

    return _pinned(
        export_tree_artifact(
            booster,
            probes=np.nan_to_num(features[:1]),
            artifact_id="lightgbm-direction-fixture",
            model_id="crypto-direction",
            model_version="1.0.0",
            dataset_hash="fixture-lightgbm-dataset",
            git_commit=FIXTURE_COMMIT,
            random_seed=3,
            as_of=FIXTURE_TIMESTAMP,
            bar_duration_minutes=5,
            lookback_periods=48,
            feature_units=dict.fromkeys(booster.feature_name(), "zscore"),
            target_units="basis_points",
        )
    )


def build_all() -> dict[str, RuntimeInferenceArtifact]:
    return {
        "har": build_har(),
        "garch": build_garch(),
        "hmm": build_hmm(),
        "lightgbm": build_lightgbm(),
    }


def write_all(destination: Path) -> list[Path]:
    """Write every fixture, returning the paths written."""
    return [
        build_all()[family].write(destination / name) for family, name in FIXTURE_NAMES.items()
    ]
