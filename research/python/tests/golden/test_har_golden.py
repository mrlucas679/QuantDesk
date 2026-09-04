from quantdesk_research.models.runtime_artifact import SupportDomain
"""HAR fitting and the artifact it exports.

The previous version of this file called itself a cross-language reproducibility test and was not
one. It exported the coefficients, recomputed the prediction from those same coefficients, and
asserted the two matched -- an identity that holds however wrong the model is, and that would have
passed unchanged while the exported coefficient names were ones the runtime refuses.
"""

from datetime import UTC, datetime

import numpy as np
import pytest

from quantdesk_research.models.har import (
    FEATURE_NAMES,
    HARModel,
    export_har_artifact,
)


def _fitted() -> HARModel:
    model = HARModel()
    model.fit(np.linspace(0.01, 0.05, 60))
    return model


def _artifact(model: HARModel):
    return export_har_artifact(
        model,
        probes=[(0.04, 0.035, 0.03), (0.01, 0.01, 0.01), (0.05, 0.02, 0.045)],
        artifact_id="har-test",
        model_id="crypto-realised-variance",
        model_version="1.0.0",
        dataset_hash="dataset-abc",
        git_commit="abc1234",
        random_seed=7,
        as_of=datetime(2026, 9, 3, tzinfo=UTC),
        bar_duration_minutes=5,
        support_domain=SupportDomain(asset_class="spot_crypto", symbols=["BTC/USD"], bar_duration_minutes=5),
        short_bars=1,
        medium_bars=5,
        long_bars=22,
        variance_units="decimal_return_variance",
    )


def test_exported_coefficients_use_the_names_the_runtime_reads() -> None:
    """The runtime reads every coefficient by name and refuses an artifact missing one.

    This module used to export ``const``/``beta_d``/``beta_w``/``beta_m``. Nothing had carried an
    artifact across yet, so the mismatch was free until the first one moved, at which point the
    runtime would have refused it as unusable parameters and reported nothing more specific.
    """
    parameters = _artifact(_fitted()).parameters
    assert set(parameters) == {"intercept", "beta_short", "beta_medium", "beta_long"}


def test_parity_cases_carry_what_the_fit_actually_predicted() -> None:
    model = _fitted()
    artifact = _artifact(model)

    for case in artifact.parity.cases:
        short, medium, long_run = case.inputs[0]
        assert case.expected[0] == pytest.approx(
            max(model.predict(short, medium, long_run), 0.0), abs=1e-15
        )


def test_feature_order_is_recorded_and_matches_the_prediction_signature() -> None:
    """Order is what the schema hash protects, so it has to be stated, not implied.

    A model fitted on short/medium/long and fed long/medium/short produces confident numbers from
    coefficients matched to the wrong inputs, and nothing downstream can tell.
    """
    artifact = _artifact(_fitted())
    assert artifact.feature_schema.feature_names == FEATURE_NAMES == [
        "rv_short",
        "rv_medium",
        "rv_long",
    ]


def test_the_artifact_hash_covers_the_coefficients() -> None:
    artifact = _artifact(_fitted())
    assert artifact.hash_matches()

    tampered = artifact.model_copy(
        update={"parameters": {**artifact.parameters, "beta_short": 99.0}}
    )
    assert not tampered.hash_matches()


def test_units_travel_with_the_model() -> None:
    """A dot product can be perfectly implemented while being fed the wrong quantity."""
    semantics = _artifact(_fitted()).feature_semantics
    assert set(semantics.units) == set(FEATURE_NAMES)
    assert all(unit == "decimal_return_variance" for unit in semantics.units.values())
    assert semantics.lookback_periods == 22
