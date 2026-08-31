import json
from pathlib import Path

import pytest

from quantdesk_research.experiments.prospective_campaign import IndependentValidationCampaign
from quantdesk_research.experiments.strategy_ensemble import run_independent_validation_campaign


def campaign_path() -> Path:
    return Path(__file__).parents[2] / "configs" / "independent_strategy_validation_campaign.json"


def literature_campaign_path() -> Path:
    return Path(__file__).parents[2] / "configs" / "literature_momentum_confirmation_campaign.json"


def test_independent_campaign_is_stable_and_disjoint() -> None:
    campaign = IndependentValidationCampaign.load(campaign_path())

    assert campaign.validation_end_exclusive <= campaign.prior_search_data_start
    assert campaign.minimum_validation_bars == 150_000
    assert campaign.round_trip_cost_bps == 60
    assert campaign.fingerprint() == IndependentValidationCampaign.load(campaign_path()).fingerprint()


def test_literature_confirmation_counts_prior_comparisons() -> None:
    campaign = IndependentValidationCampaign.load(literature_campaign_path())

    assert campaign.prior_comparisons == 32
    assert campaign.strategy_families == (
        "weekly_time_series_momentum",
        "four_week_time_series_momentum",
        "dual_horizon_momentum",
        "four_week_breakout",
    )


def test_independent_campaign_rejects_overlap(tmp_path: Path) -> None:
    payload = json.loads(campaign_path().read_text(encoding="utf-8"))
    payload["validation_end_exclusive"] = "2025-01-01T00:00:00+00:00"
    configured = tmp_path / "campaign.json"
    configured.write_text(json.dumps(payload), encoding="utf-8")

    with pytest.raises(ValueError, match="overlaps searched evidence"):
        IndependentValidationCampaign.load(configured)


def test_independent_campaign_fails_closed_on_incomplete_broker_cohort(tmp_path: Path) -> None:
    (tmp_path / "bars.json").write_text(
        '[{"t":"2022-01-01T00:00:00+00:00"}]', encoding="utf-8"
    )
    (tmp_path / "independent-validation-manifest.json").write_text(
        '{"symbol":"BTC/USD","timeframe":"5Min","dataFile":"bars.json"}',
        encoding="utf-8",
    )

    with pytest.raises(ValueError, match="INDEPENDENT_VALIDATION_INSUFFICIENT:1/150000"):
        run_independent_validation_campaign(tmp_path, campaign_path())
