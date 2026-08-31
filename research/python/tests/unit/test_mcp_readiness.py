from pathlib import Path
from unittest.mock import patch

from quantdesk_research.mcp.server import _prospective_campaign_status


def campaign_path() -> Path:
    return Path(__file__).parents[2] / "configs" / "prospective_strategy_campaign.json"


def test_prospective_status_reports_collection_without_claiming_readiness(tmp_path: Path) -> None:
    data_root = tmp_path / "data"
    data_root.mkdir()
    (data_root / "bars.json").write_text(
        '[{"t":"2026-08-31T03:35:00+00:00"}]', encoding="utf-8"
    )
    (data_root / "latest-manifest.json").write_text(
        '{"dataFile":"bars.json"}', encoding="utf-8"
    )

    with (
        patch("quantdesk_research.mcp.server.get_research_config") as config,
        patch(
            "quantdesk_research.mcp.server.Path",
            side_effect=lambda value: campaign_path()
            if value == "configs/prospective_strategy_campaign.json"
            else Path(value),
        ),
    ):
        config.return_value.data_root = data_root
        status = _prospective_campaign_status()

    assert status["status"] == "COLLECTING_UNSEEN_EVIDENCE"
    assert status["evidence_ready"] is False
    assert status["unseen_bars"] == 1
    assert status["required_unseen_bars"] == 8_640


def test_prospective_status_fails_closed_when_evidence_is_missing(tmp_path: Path) -> None:
    with patch("quantdesk_research.mcp.server.get_research_config") as config:
        config.return_value.data_root = tmp_path
        status = _prospective_campaign_status()

    assert status == {"evidence_ready": False, "status": "UNAVAILABLE"}
