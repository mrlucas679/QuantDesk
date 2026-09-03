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
from quantdesk_research.models.runtime_artifact import SupportDomain
from quantdesk_research.models.regime_hmm import export_regime_hmm_artifact, fit_regime_hmm
from quantdesk_research.models.runtime_artifact import RuntimeInferenceArtifact
from quantdesk_research.models.tree_export import export_tree_artifact
from quantdesk_research.runtime.model_fitting import (
    LONG_BARS,
    MEDIUM_BARS,
    SHORT_BARS,
    har_design,
)

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


#: Every fixture is fitted on the same synthetic BTC/USD five-minute series, and now says so.
#: A fixture that declared a wider domain than it was built from would make the runtime's support
#: check pass in tests for reasons that do not hold in production.
FIXTURE_DOMAIN = SupportDomain(
    asset_class="spot_crypto", symbols=["BTC/USD"], bar_duration_minutes=5
)


def build_har() -> RuntimeInferenceArtifact:
    """A HAR fit at the windows the runtime actually serves, not the daily convention.

    This fixture previously fitted 1 / 5 / 22 -- the daily HAR convention -- and then labelled
    itself with whatever windows the exporter was told. That made it a fixture of a model the
    runtime would compute different features for, and its schema hash was one the runtime could
    never derive. Building the design the way production builds it keeps the fixture a faithful
    miniature rather than a plausible-looking one.
    """
    rng = np.random.default_rng(5)
    closes = 30_000.0 * np.exp(
        np.cumsum(rng.normal(0.0, 0.0012, size=SHORT_BARS + LONG_BARS + 600))
    )
    design, target, _ = har_design(closes)

    model = HARModel()
    model.fit_matrix(design, target)

    return _pinned(
        export_har_artifact(
            model,
            probes=[(float(row[0]), float(row[1]), float(row[2])) for row in design[:4]],
            artifact_id="har-realised-variance-fixture",
            model_id="crypto-realised-variance",
            model_version="1.0.0",
            dataset_hash="fixture-har-dataset",
            git_commit=FIXTURE_COMMIT,
            random_seed=0,
            as_of=FIXTURE_TIMESTAMP,
            bar_duration_minutes=5,
            support_domain=FIXTURE_DOMAIN,
            short_bars=SHORT_BARS,
            medium_bars=MEDIUM_BARS,
            long_bars=LONG_BARS,
            variance_units="mean_squared_log_return",
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
            support_domain=FIXTURE_DOMAIN,
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
            support_domain=FIXTURE_DOMAIN,
            lookback_periods=360,
            feature_units={"vol_percentile": "percentile", "adx_normalised": "ratio"},
        )
    )


def build_lightgbm() -> RuntimeInferenceArtifact:
    """A booster fitted with NaNs present, so its nodes carry the ``NaN`` missing convention.

    Deliberately the convention the first C# traversal happened to implement, and the seed is
    chosen so the probe grid can tell it apart from the two it got wrong. That is not automatic: on
    the first six seeds tried, the fitted structure put every split where the conventions disagree
    behind a default branch that steered missing values away from it, so all three scored
    identically on forty thousand random inputs. A fixture built from one of those would have loaded
    happily against the broken traversal.
    """
    rng = np.random.default_rng(19)
    features = np.where(rng.random((1000, 4)) < 0.3, np.nan, rng.normal(size=(1000, 4)))
    target = (
        np.nan_to_num(features[:, 0]) * 2.0
        - np.nan_to_num(features[:, 1])
        + rng.normal(scale=0.1, size=1000)
    )

    # Split by position rather than at random. These are stand-ins for a time series, and a random
    # split would let the fit see rows either side of every held-out one -- which is how a held-out
    # score comes back flattering and means nothing.
    train_features, train_target = features[:800], target[:800]
    test_features, test_target = features[800:], target[800:]

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
            lgb.Dataset(train_features, label=train_target),
            num_boost_round=5,
        )

    return _pinned(
        export_tree_artifact(
            booster,
            probes=np.nan_to_num(train_features[:1]),
            artifact_id="lightgbm-direction-fixture",
            model_id="crypto-direction",
            model_version="1.0.0",
            dataset_hash="fixture-lightgbm-dataset",
            git_commit=FIXTURE_COMMIT,
            random_seed=3,
            as_of=FIXTURE_TIMESTAMP,
            bar_duration_minutes=5,
            support_domain=FIXTURE_DOMAIN,
            lookback_periods=48,
            feature_units=dict.fromkeys(booster.feature_name(), "zscore"),
            target_units="basis_points",
            held_out_features=test_features,
            held_out_target=test_target,
            training_mean=float(train_target.mean()),
            minimum_skill=0.5,
            require_missing_discrimination=True,
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
