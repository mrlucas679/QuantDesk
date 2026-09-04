"""The families a forecast can belong to, and which gates each answers to.

The publisher accepted only ``directional_return_bps``, so the volatility and regime models had no
honest route across the boundary. That was not an oversight so much as a category error left
unresolved: every execution gate asks whether a signal is worth trading, and a variance forecast
does not trade. These tests pin the resolution -- the gates are not relaxed, they are addressed to
the family that answers them.
"""

from datetime import UTC, datetime
from pathlib import Path

import pytest

from quantdesk_research.contracts.feature_schema import FeatureSchema
from quantdesk_research.contracts.forecast import Forecast, ForecastUncertainty
from quantdesk_research.contracts.forecast_family import (
    FORECAST_FAMILIES,
    ForecastFamilyError,
    family_of,
)
from quantdesk_research.models.contract_publication import ContractPublisher
from quantdesk_research.models.fixture_generation import build_hmm
from quantdesk_research.models.model_registry import ModelRegistry

SCHEMA = FeatureSchema(
    schema_version="v1",
    feature_names=["a"],
    dtypes={"a": "float64"},
    normalization={},
    lookback_periods=10,
    source_requirements=["alpaca_ohlcv"],
    feature_hash="schema-hash",
)


def _forecast(**overrides: object) -> Forecast:
    base: dict[str, object] = {
        "expert_id": "e",
        "model_id": "m",
        "model_version": "v1",
        "instrument": "BTC/USD",
        "as_of_time": datetime.now(UTC),
        "forecast_family": "conditional_variance",
        "horizon_minutes": 5,
        "point_forecast": 0.0004,
        "units": "squared_percent_return",
        "confidence": 0.7,
        "calibration_status": "validated",
        "support_domain_status": "in_domain",
        "feature_schema_hash": "schema-hash",
        "artifact_hash": "artifact-hash",
        "status": "valid",
    }
    base.update(overrides)
    return Forecast(**base)  # type: ignore[arg-type]


def _publisher(tmp_path: Path) -> ContractPublisher:
    return ContractPublisher(tmp_path / "artifacts", ModelRegistry(str(tmp_path / "r.db")))


def test_exactly_one_family_may_license_a_trade() -> None:
    """Named rather than inferred, so a family added later cannot trade by accident."""
    licensing = [name for name, spec in FORECAST_FAMILIES.items() if spec.licenses_execution]
    assert licensing == ["directional_return_bps"]


def test_an_unregistered_family_is_refused_rather_than_defaulted() -> None:
    """A default would make the newest and least understood family inherit the oldest's guarantees."""
    with pytest.raises(ForecastFamilyError, match="not registered"):
        family_of("vibes")


def test_an_advisory_family_is_not_asked_to_prove_a_net_edge() -> None:
    """Asking a variance forecast for evidence it earns basis points gets a placeholder back, and a
    placeholder reads like evidence."""
    variance = FORECAST_FAMILIES["conditional_variance"]
    assert not variance.licenses_execution
    assert "R11" not in variance.required_gates
    family_of("conditional_variance").validate(_forecast())


def test_a_negative_variance_fails_rather_than_being_clamped() -> None:
    """A model reporting outside its own range is a fault, and clamping it on the way out hides
    which forecasts were affected."""
    with pytest.raises(ForecastFamilyError, match="negative variance"):
        family_of("conditional_variance").validate(_forecast(point_forecast=-1e-6))


def test_a_variance_without_units_is_refused() -> None:
    """Percent and decimal returns differ by four orders of magnitude in a variance, and the figure
    itself does not say which it is."""
    with pytest.raises(ForecastFamilyError, match="without units"):
        family_of("conditional_variance").validate(_forecast(units=None))


def test_a_regime_posterior_must_be_a_distribution_over_named_states() -> None:
    spec = family_of("regime_probability")
    spec.validate(
        _forecast(
            forecast_family="regime_probability",
            point_forecast=0.0,
            distribution={"calm": 0.2, "normal": 0.7, "stress": 0.1},
        )
    )

    with pytest.raises(ForecastFamilyError, match="sum to"):
        spec.validate(
            _forecast(
                forecast_family="regime_probability",
                distribution={"calm": 0.2, "normal": 0.5, "stress": 0.1},
            )
        )


def test_a_regime_forecast_without_state_names_is_refused() -> None:
    """State 0 today is not the same regime as state 0 tomorrow.

    A posterior travelling without its vocabulary lets a retrain permute the latent states and make
    every regime-change interrupt fire on a change that did not happen.
    """
    with pytest.raises(ForecastFamilyError, match="must be named"):
        family_of("regime_probability").validate(
            _forecast(forecast_family="regime_probability", distribution={" ": 1.0})
        )


def test_a_directional_forecast_without_uncertainty_is_refused() -> None:
    """The consuming gate already declined one. Publication declines it too, rather than emitting
    something the far side will reject."""
    with pytest.raises(ForecastFamilyError, match="no claim attached"):
        family_of("directional_return_bps").validate(
            _forecast(forecast_family="directional_return_bps", point_forecast=12.0, units="bps")
        )


def test_a_net_edge_with_no_observations_behind_it_is_refused() -> None:
    with pytest.raises(ForecastFamilyError, match="no observations"):
        family_of("directional_return_bps").validate(
            _forecast(
                forecast_family="directional_return_bps",
                point_forecast=12.0,
                uncertainty=ForecastUncertainty(
                    standard_error_bps=4.0,
                    historical_net_edge_bps=3.5,
                    historical_net_edge_standard_error_bps=1.0,
                    historical_observations=0,
                    assumed_round_trip_cost_bps=33.7,
                ),
            )
        )


def test_an_advisory_forecast_publishes_through_its_own_path(tmp_path: Path) -> None:
    published = _publisher(tmp_path).publish_forecast(SCHEMA, _forecast(), "artifact-hash")
    assert (tmp_path / "artifacts" / published).exists()


def test_an_advisory_forecast_cannot_be_pushed_through_the_execution_bundle(tmp_path: Path) -> None:
    """The gates are not weakened, they are addressed. A variance forecast reaching the execution
    bundle would be answering the wrong question with the right paperwork."""
    publisher = _publisher(tmp_path)
    with pytest.raises(ValueError, match="does not license a trade"):
        publisher.publish_validated(SCHEMA, _artifact_stub(), _forecast(), tmp_path / "a.json")


def test_a_forecast_pointing_at_the_wrong_artifact_is_refused(tmp_path: Path) -> None:
    with pytest.raises(ValueError, match="artifact hash"):
        _publisher(tmp_path).publish_forecast(SCHEMA, _forecast(), "some-other-artifact")


def test_a_fitted_model_publishes_without_claiming_a_licence_to_trade(tmp_path: Path) -> None:
    """A fitted model and a strategy are separate lifecycles. This carries the numbers an inference
    path needs; gaining permission to trade on them is a different decision taken elsewhere."""
    published = _publisher(tmp_path).publish_model(build_hmm())
    assert (tmp_path / "artifacts" / published).exists()


def test_a_model_edited_after_sealing_is_refused(tmp_path: Path) -> None:
    tampered = build_hmm()
    tampered = tampered.model_copy(
        update={"parameters": {**tampered.parameters, "start_0": 0.5}}
    )

    with pytest.raises(ValueError, match="edited after sealing"):
        _publisher(tmp_path).publish_model(tampered)


def _artifact_stub() -> object:
    """The execution bundle refuses on family before it reads the artifact, so this never runs."""

    class _Never:
        def __getattr__(self, name: str) -> object:
            raise AssertionError(
                f"the artifact should not be read once the family is refused, but {name} was"
            )

    return _Never()
