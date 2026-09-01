"""The research plane's half of the realised-cost pin.

The execution plane publishes this contract because it owns the only ground truth: account equity
before and after each round trip. This test reads the same committed fixture the C# producer test
serialises against, so the two planes are pinned to one artifact rather than to two independent
opinions about its shape -- which is how a 60 bps assumption in Python, a 50 bps schedule rate in
C#, and a 68 bps measured reality coexisted without anything noticing.
"""

from __future__ import annotations

import json
from pathlib import Path

import pytest

from quantdesk_research.backtest.realised_costs import (
    CONTRACT_FILENAME,
    RealisedCostUnavailableError,
    load_realised_costs,
)

FIXTURE_ROOT = Path(__file__).resolve().parents[4] / "tests" / "fixtures" / "research-contracts"


def test_the_published_contract_loads_through_the_research_reader() -> None:
    dataset = load_realised_costs(FIXTURE_ROOT)

    assert dataset.asset_class == "crypto"
    assert dataset.execution_mode == "PAPER"
    assert dataset.observation_count == 7


def test_the_charged_cost_is_the_bound_and_varies_with_order_size() -> None:
    # Cost is a curve. Charging one average understates it for large orders and overstates it for
    # small ones, and the second error rejects real edges as surely as the first accepts false ones.
    dataset = load_realised_costs(FIXTURE_ROOT)

    assert dataset.round_trip_bps(10.0) == pytest.approx(71.2)
    assert dataset.round_trip_bps(50.0) == pytest.approx(74.3)


def test_the_bound_is_charged_rather_than_the_mean() -> None:
    # This number is subtracted from an edge to decide whether to trade. A mean is right half the
    # time, so charging it accepts every candidate whose edge sits inside the measurement error.
    dataset = load_realised_costs(FIXTURE_ROOT)
    small = dataset.buckets[0]

    assert dataset.round_trip_bps(10.0) > small.mean_bps


def test_an_unmeasured_order_size_refuses_rather_than_extrapolating() -> None:
    dataset = load_realised_costs(FIXTURE_ROOT)

    with pytest.raises(RealisedCostUnavailableError, match="No measured round trip covers"):
        dataset.round_trip_bps(5_000.0)


def test_a_missing_dataset_refuses_rather_than_defaulting(tmp_path: Path) -> None:
    # The failure that mattered. A silent default is how three disagreeing cost numbers survived:
    # a run that cannot say what trading costs cannot say whether a strategy is profitable.
    with pytest.raises(RealisedCostUnavailableError, match="No realised-cost dataset"):
        load_realised_costs(tmp_path)


def test_every_reported_number_names_the_trips_behind_it(tmp_path: Path) -> None:
    dataset = load_realised_costs(FIXTURE_ROOT)

    for bucket in dataset.buckets:
        assert len(bucket.source_record_ids) == bucket.round_trip_count
        assert all(identifier for identifier in bucket.source_record_ids)
    assert "7 PAPER crypto round trips on alpaca" in dataset.provenance()


def test_an_empty_dataset_is_refused_rather_than_read_as_free(tmp_path: Path) -> None:
    payload = json.loads((FIXTURE_ROOT / CONTRACT_FILENAME).read_text(encoding="utf-8"))
    payload["buckets"] = []
    (tmp_path / CONTRACT_FILENAME).write_text(json.dumps(payload), encoding="utf-8")

    with pytest.raises(RealisedCostUnavailableError, match="no measured buckets"):
        load_realised_costs(tmp_path)
