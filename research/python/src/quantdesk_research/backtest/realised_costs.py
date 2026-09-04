"""Reads the realised-cost dataset the execution plane publishes.

Why the research plane must not hold its own number
---------------------------------------------------
Four entry points in this package defaulted to a 60 bps round trip. The C# cost scenarios charged
Alpaca's published 50 bps schedule rate. The account, measured across 59 live BTC/USD round trips,
lost 68 bps. Three numbers, none of them agreeing, each written down in a different language, and
the two that governed decisions were both below the one that was actually true — so a strategy
whose real edge sat between them passed research and lost money in execution.

The execution plane owns this measurement because it owns the only ground truth: account equity
before and after each round trip. Alpaca charges a "Coin Pair Transaction Fee (USD)" that appears
in neither the fill price nor the filled quantity, so a cost derived from fills is not merely less
precise, it is systematically low. This module therefore reads what that plane publishes and offers
no default of its own.

The absence of a dataset is not a cost of zero. Every function here fails loudly rather than
substituting a number, because a silent fallback is exactly how the three-way disagreement above
survived as long as it did.
"""

from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path

CONTRACT_FILENAME = "realised-costs.json"


class RealisedCostUnavailableError(RuntimeError):
    """Raised when no measured cost covers what the caller is about to assume."""


@dataclass(frozen=True)
class RealisedCostBucket:
    """Measured round-trip cost for one notional band."""

    min_notional: float
    max_notional: float | None
    round_trip_count: int
    median_bps: float
    mean_bps: float
    upper_confidence_bps: float
    source_record_ids: tuple[str, ...]

    def covers(self, notional: float) -> bool:
        return notional >= self.min_notional and (
            self.max_notional is None or notional < self.max_notional
        )


@dataclass(frozen=True)
class RealisedCostDataset:
    """A versioned, provenance-carrying record of what trading actually cost."""

    dataset_id: str
    dataset_version: str
    asset_class: str
    venue: str
    execution_mode: str
    observed_from: str
    observed_to: str
    buckets: tuple[RealisedCostBucket, ...]

    @property
    def observation_count(self) -> int:
        return sum(bucket.round_trip_count for bucket in self.buckets)

    def round_trip_bps(self, notional: float) -> float:
        """The cost to charge an order of this size, as an upper confidence bound.

        The bound rather than the mean, because this figure is subtracted from an edge to decide
        whether a strategy is worth trading. Charging the mean accepts every candidate whose edge
        sits inside the measurement error.
        """
        for bucket in self.buckets:
            if bucket.covers(notional):
                return bucket.upper_confidence_bps
        raise RealisedCostUnavailableError(
            f"No measured round trip covers a notional of {notional:,.2f}. "
            f"Measured bands: {[(b.min_notional, b.max_notional) for b in self.buckets]}. "
            "Extrapolating would invent the evidence this dataset exists to carry."
        )

    def provenance(self) -> str:
        """One line naming what the number rests on, for a report that has to be auditable."""
        return (
            f"{self.dataset_id}@{self.dataset_version}: {self.observation_count} "
            f"{self.execution_mode} {self.asset_class} round trips on {self.venue}, "
            f"{self.observed_from} to {self.observed_to}"
        )


def load_realised_costs(data_root: Path | str) -> RealisedCostDataset:
    """Load the published dataset, or refuse.

    Refusing is the point. A research run that cannot say what trading costs cannot say whether a
    strategy is profitable, and returning a plausible-looking default would let it claim otherwise.
    """
    path = Path(data_root) / CONTRACT_FILENAME
    if not path.exists():
        raise RealisedCostUnavailableError(
            f"No realised-cost dataset at {path}. Publish one from the execution plane "
            "(GET /api/costs/realised) before running a campaign that charges costs."
        )

    payload = json.loads(path.read_text(encoding="utf-8"))
    buckets = tuple(
        RealisedCostBucket(
            min_notional=float(item["minNotional"]),
            max_notional=None if item.get("maxNotional") is None else float(item["maxNotional"]),
            round_trip_count=int(item["roundTripCount"]),
            median_bps=float(item["medianBps"]),
            mean_bps=float(item["meanBps"]),
            upper_confidence_bps=float(item["upperConfidenceBps"]),
            source_record_ids=tuple(item["sourceRecordIds"]),
        )
        for item in payload["buckets"]
    )
    if not buckets:
        raise RealisedCostUnavailableError(f"The dataset at {path} carries no measured buckets.")

    return RealisedCostDataset(
        dataset_id=str(payload["datasetId"]),
        dataset_version=str(payload["datasetVersion"]),
        asset_class=str(payload["assetClass"]),
        venue=str(payload["venue"]),
        execution_mode=str(payload["executionMode"]),
        observed_from=str(payload["observedFrom"]),
        observed_to=str(payload["observedTo"]),
        buckets=buckets,
    )


# The notional the autonomous lane actually sizes to. Cost is a curve, so a research run that wants
# to describe live trading has to ask the curve about the size live trading uses, not an average
# over sizes it never sends.
RESEARCH_NOTIONAL_USD = 20.0


def resolve_round_trip_bps(
    data_root: Path | str,
    explicit_bps: float | None = None,
    notional: float = RESEARCH_NOTIONAL_USD,
) -> tuple[float, str]:
    """Return the cost to charge and a line saying where it came from.

    Provenance is returned rather than logged so a caller cannot report a result without also being
    able to report what cost produced it. The three-way disagreement this module exists to end --
    60 bps assumed in research, 50 bps charged in execution, 68 bps actually paid -- survived
    precisely because every number looked equally authoritative at the point of use.

    An explicit override is honoured, because a sensitivity run has to be able to ask what happens
    at a cost nobody has measured. It is labelled an assumption so the answer cannot be mistaken for
    a measurement.
    """
    if explicit_bps is not None:
        return explicit_bps, f"ASSUMED {explicit_bps:.1f} bps (operator override, not measured)"

    dataset = load_realised_costs(data_root)
    return dataset.round_trip_bps(notional), f"MEASURED {dataset.provenance()}"
