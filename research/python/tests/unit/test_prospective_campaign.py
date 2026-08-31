import json
from pathlib import Path

import pytest

from quantdesk_research.experiments.prospective_campaign import ProspectiveCampaign


def campaign_path() -> Path:
    return Path(__file__).parents[2] / "configs" / "prospective_strategy_campaign.json"


def test_checked_in_campaign_is_valid_and_stably_fingerprinted() -> None:
    first = ProspectiveCampaign.load(campaign_path())
    second = ProspectiveCampaign.load(campaign_path())

    assert first.fingerprint() == second.fingerprint()
    assert len(first.fingerprint()) == 64
    assert len(first.strategy_families) == 8


def test_holdout_excludes_cutoff_and_requires_unseen_minimum() -> None:
    campaign = ProspectiveCampaign.load(campaign_path())
    bars = [
        {"t": "2026-08-31T03:30:00Z"},
        {"t": "2026-08-31T03:35:00Z"},
    ]

    assert campaign.unseen_bar_count(bars) == 1
    with pytest.raises(ValueError, match="PROSPECTIVE_HOLDOUT_INSUFFICIENT:1/8640"):
        campaign.require_sufficient_unseen_data(bars)


def test_campaign_rejects_weakened_cost_gate(tmp_path: Path) -> None:
    payload = json.loads(campaign_path().read_text(encoding="utf-8"))
    payload["round_trip_cost_bps"] = 10
    weakened = tmp_path / "weakened.json"
    weakened.write_text(json.dumps(payload), encoding="utf-8")

    with pytest.raises(ValueError, match="Economic qualification gates"):
        ProspectiveCampaign.load(weakened)
