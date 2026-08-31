from __future__ import annotations

from dataclasses import dataclass


@dataclass(frozen=True)
class EquityCostScenario:
    """Conservative round-trip assumptions for small US-equity paper orders."""

    name: str
    spread_and_slippage_bps: float
    regulatory_and_rounding_bps: float
    commission_bps: float

    @property
    def round_trip_bps(self) -> float:
        """Return the all-in round-trip cost in basis points."""
        return (
            self.spread_and_slippage_bps
            + self.regulatory_and_rounding_bps
            + self.commission_bps
        )

    def net_return(self, gross_return: float) -> float:
        """Deduct one modeled round trip from a decimal gross return."""
        return gross_return - self.round_trip_bps / 10_000


BASE_COST = EquityCostScenario(
    name="BASE",
    spread_and_slippage_bps=20.0,
    regulatory_and_rounding_bps=5.0,
    commission_bps=0.0,
)
STRESS_COST = EquityCostScenario(
    name="STRESS",
    spread_and_slippage_bps=30.0,
    regulatory_and_rounding_bps=5.0,
    commission_bps=0.0,
)
SEVERE_COST = EquityCostScenario(
    name="SEVERE",
    spread_and_slippage_bps=45.0,
    regulatory_and_rounding_bps=5.0,
    commission_bps=0.0,
)

COST_SCENARIOS = (BASE_COST, STRESS_COST, SEVERE_COST)
