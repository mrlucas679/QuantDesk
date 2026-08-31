import json
from pathlib import Path

from quantdesk_research.runtime.research_worker import (
    IndependentValidationRegistration,
    validate_independent_campaign,
)


def _write_campaign(path: Path) -> None:
    path.write_text(
        json.dumps(
            {
                "campaign_id": "TEST-INDEPENDENT-001",
                "instrument": "BTC/USD",
                "timeframe": "5Min",
                "registered_at": "2026-08-31T08:30:00+00:00",
                "validation_start_inclusive": "2022-01-01T00:00:00+00:00",
                "validation_end_exclusive": "2024-01-01T00:00:00+00:00",
                "prior_search_data_start": "2024-08-29T07:45:00+00:00",
                "minimum_validation_bars": 150000,
                "round_trip_cost_bps": 60,
                "minimum_trades": 60,
                "minimum_sharpe": 0.5,
                "required_lower_confidence_bps": 0,
                "strategy_families": ["moving_average_trend"],
                "holding_horizons_bars": [48],
                "prior_comparisons": 12,
            }
        ),
        encoding="utf-8",
    )


def test_independent_validation_waits_for_manifest(tmp_path: Path) -> None:
    configs = tmp_path / "configs"
    configs.mkdir()
    _write_campaign(configs / "campaign.json")

    result = validate_independent_campaign(
        tmp_path / "data",
        configs,
        tmp_path / "artifacts",
        IndependentValidationRegistration("campaign.json", "manifest.json"),
    )

    assert result is None
    assert not (tmp_path / "artifacts").exists()


def test_independent_validation_reuses_frozen_outcome_without_evaluation(
    tmp_path: Path,
) -> None:
    configs = tmp_path / "configs"
    configs.mkdir()
    campaign_path = configs / "campaign.json"
    _write_campaign(campaign_path)
    from quantdesk_research.experiments.prospective_campaign import IndependentValidationCampaign

    campaign = IndependentValidationCampaign.load(campaign_path)
    outcome_path = (
        tmp_path
        / "artifacts"
        / "independent-validation"
        / f"{campaign.campaign_id}-{campaign.fingerprint()}.json"
    )
    outcome_path.parent.mkdir(parents=True)
    expected = {
        "campaign_id": campaign.campaign_id,
        "campaign_fingerprint": campaign.fingerprint(),
        "passed": False,
        "results": [],
    }
    outcome_path.write_text(json.dumps(expected), encoding="utf-8")

    result = validate_independent_campaign(
        tmp_path / "missing-data",
        configs,
        tmp_path / "artifacts",
        IndependentValidationRegistration("campaign.json", "manifest.json"),
    )

    assert result == expected


def test_write_once_never_replaces_frozen_validation(tmp_path: Path) -> None:
    from quantdesk_research.runtime.research_worker import _write_json_once

    target = tmp_path / "outcome.json"
    _write_json_once(target, {"passed": False})
    _write_json_once(target, {"passed": True})

    assert json.loads(target.read_text(encoding="utf-8")) == {"passed": False}
