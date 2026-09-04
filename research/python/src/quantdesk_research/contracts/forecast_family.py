"""What each forecast family is, and which gates apply to it.

Why the publisher only accepted one family
------------------------------------------
``ContractPublisher`` refused anything but ``directional_return_bps``, which looked like an
oversight and was not quite one. Every gate it enforces -- R0 through R12, transfer grade, primary
evidence, net edge after costs -- exists to answer whether a signal is worth trading. That question
only makes sense for a forecast that licenses a trade.

A conditional variance does not license a trade. It sizes one, or refuses one, or ends one early.
Demanding that it demonstrate a positive net edge after round-trip costs is not a stricter standard;
it is the wrong question, and the honest answers to it are all "not applicable" -- which is how a
gate turns into a form to be filled in.

So the fix is not to relax the gates. It is to say which gates each family answers to. A directional
forecast still carries every execution gate it did before. A variance forecast carries calibration
evidence instead, because being well calibrated is what makes it useful and QLIKE is how that gets
measured. A regime posterior carries a proper distribution and a stated state vocabulary, because
the failure that matters there is a relabelled state making "the regime changed" fire on a retrain.

The property that keeps this from becoming a loophole
-----------------------------------------------------
Exactly one family may license a trade, and it is named here rather than inferred. A new family
added without thinking about it cannot trade by default -- ``licenses_execution`` is false unless
someone writes otherwise, and writing otherwise means arriving at this file and reading why.
"""

from __future__ import annotations

import math
from dataclasses import dataclass
from typing import Any

#: Gates a forecast must carry before it may reach the execution plane.
EXECUTION_GATES = frozenset({"R0", "R1", "R2", "R3", "R4", "R5", "R6", "R7", "R11", "R12"})

#: Gates a forecast that only informs a decision must carry. Calibration rather than net edge:
#: a variance forecast is useful when it is right about magnitude, not when it earns basis points.
ADVISORY_GATES = frozenset({"R0", "R1", "R2"})


class ForecastFamilyError(ValueError):
    """A forecast does not satisfy the family it claims to belong to."""


@dataclass(frozen=True)
class ForecastFamilySpec:
    """One family, its gates, and what a well-formed forecast of it looks like."""

    name: str
    licenses_execution: bool
    required_gates: frozenset[str]
    description: str

    def validate(self, forecast: Any) -> None:
        """Raise unless this forecast is well formed for the family."""
        if forecast.forecast_family != self.name:
            raise ForecastFamilyError(
                f"forecast claims family {forecast.forecast_family!r}, validated as {self.name!r}"
            )
        _VALIDATORS[self.name](forecast)


def _validate_directional(forecast: Any) -> None:
    """A return in basis points, with what it cost to earn and how wrong it might be.

    The uncertainty block is required because a point forecast answers neither question, and the
    consuming gate refuses a forecast that omits it rather than reading the silence as certainty.
    """
    if not math.isfinite(forecast.point_forecast):
        raise ForecastFamilyError("a directional forecast must be a finite number of basis points")
    if forecast.uncertainty is None:
        raise ForecastFamilyError(
            "a directional forecast without uncertainty is a number with no claim attached; "
            "the execution gate cannot size or refuse it"
        )
    if forecast.uncertainty.standard_error_bps < 0.0:
        raise ForecastFamilyError("standard error cannot be negative")
    if forecast.uncertainty.historical_observations <= 0:
        raise ForecastFamilyError(
            "a net-edge figure with no observations behind it is an assertion, not evidence"
        )
    if forecast.uncertainty.assumed_round_trip_cost_bps < 0.0:
        raise ForecastFamilyError(
            "the assumed round-trip cost must be stated and non-negative, or execution cannot tell "
            "whether it has already been charged"
        )


def _validate_conditional_variance(forecast: Any) -> None:
    """A variance, which cannot be negative, and must say what it is a variance *of*.

    No net-edge requirement. A variance forecast does not trade, and asking it to demonstrate one
    would be answered with a placeholder -- which is worse than not asking, because a placeholder
    reads like evidence.
    """
    if not math.isfinite(forecast.point_forecast):
        raise ForecastFamilyError("a variance forecast must be a finite number")
    if forecast.point_forecast < 0.0:
        raise ForecastFamilyError(
            "a negative variance is not a small variance; it is a model reporting outside its own "
            "range and must fail rather than be clamped on the way out"
        )
    if not forecast.units:
        raise ForecastFamilyError(
            "a variance without units cannot be compared to anything; percent and decimal returns "
            "differ by four orders of magnitude in it"
        )


def _validate_regime_probability(forecast: Any) -> None:
    """A posterior over named states: non-negative, summing to one, and labelled.

    The labels are the point. State 0 from today's fit is not the same regime as state 0 from
    tomorrow's -- Baum-Welch has no notion of which state is calm -- so a posterior that travels
    without its vocabulary lets a retrain permute the states and make every regime-change interrupt
    fire on a change that did not happen.
    """
    distribution = forecast.distribution
    if not distribution:
        raise ForecastFamilyError("a regime forecast is a distribution, not a single number")

    for state, probability in distribution.items():
        if not state.strip():
            raise ForecastFamilyError("every regime state must be named")
        if not math.isfinite(probability) or probability < 0.0:
            raise ForecastFamilyError(f"probability for {state!r} is not a probability")

    total = math.fsum(distribution.values())
    if abs(total - 1.0) > 1e-6:
        raise ForecastFamilyError(f"regime probabilities sum to {total}, not one")


_VALIDATORS = {
    "directional_return_bps": _validate_directional,
    "conditional_variance": _validate_conditional_variance,
    "regime_probability": _validate_regime_probability,
}


FORECAST_FAMILIES: dict[str, ForecastFamilySpec] = {
    "directional_return_bps": ForecastFamilySpec(
        name="directional_return_bps",
        licenses_execution=True,
        required_gates=EXECUTION_GATES,
        description="An expected return in basis points, net of the cost this plane assumed.",
    ),
    "conditional_variance": ForecastFamilySpec(
        name="conditional_variance",
        licenses_execution=False,
        required_gates=ADVISORY_GATES,
        description="The variance expected next period, for sizing and for refusing.",
    ),
    "regime_probability": ForecastFamilySpec(
        name="regime_probability",
        licenses_execution=False,
        required_gates=ADVISORY_GATES,
        description="A posterior over named market states, for gating and for exits.",
    ),
}


def family_of(name: str) -> ForecastFamilySpec:
    """The spec for a family, or a refusal naming what is registered.

    Unknown families are refused rather than defaulted. A default would make the newest and least
    understood family inherit the guarantees of the oldest.
    """
    spec = FORECAST_FAMILIES.get(name)
    if spec is None:
        raise ForecastFamilyError(
            f"forecast family {name!r} is not registered; known families are "
            f"{sorted(FORECAST_FAMILIES)}"
        )
    return spec
