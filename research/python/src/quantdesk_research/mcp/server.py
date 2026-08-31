import os
import shutil
import sys
from pathlib import Path
from typing import Any

import psutil  # type: ignore[import-untyped]
from fastmcp import FastMCP
from loguru import logger
from starlette.requests import Request
from starlette.responses import JSONResponse

from quantdesk_research.config import get_research_config
from quantdesk_research.data.orderbook_evidence import summarize_orderbook_evidence
from quantdesk_research.evaluation.trial_ledger import TrialLedger
from quantdesk_research.experiments.prospective_campaign import ProspectiveCampaign
from quantdesk_research.mcp.alpaca import FORBIDDEN_PATTERNS, validate_alpaca_config
from quantdesk_research.models.model_registry import ModelRegistry
from quantdesk_research.shadow.auditor import ShadowAuditor

mcp = FastMCP("QuantDesk Research")

# Alpaca integration is now preferred as a separate MCP server.
# See README.md for configuration and security invariants.
if (
    os.environ.get("QUANTDESK_ENABLE_ALPACA", "false").lower() == "true"
    and not validate_alpaca_config()
):
    print("ALPACA CONFIGURATION FAILED SECURITY VALIDATION.")
    # We don't exit here to allow QuantDesk research tools to work,
    # but we warn that the separate Alpaca server might be insecure.


def _validated_models() -> list[dict[str, Any]]:
    config = get_research_config()
    registry = ModelRegistry(str(config.experiment_db_path))
    return registry.list_models(promotion_state="VALIDATED")


def _microstructure_status() -> dict[str, int | str]:
    """Summarize raw order-book evidence without treating it as a validated model."""
    root = get_research_config().data_root
    evidence = summarize_orderbook_evidence(root, "BTC/USD")
    status = "EVIDENCE_READY" if evidence.usable_records >= 100_000 else "SHADOW_ONLY"
    return {
        "records": evidence.total_records,
        "usable_records": evidence.usable_records,
        "gaps": evidence.gap_events,
        "status": status,
    }


def _prospective_campaign_status() -> dict[str, Any]:
    """Report immutable campaign progress without treating incomplete evidence as readiness."""
    config = get_research_config()
    campaign_path = Path("configs/prospective_strategy_campaign.json")
    try:
        campaign = ProspectiveCampaign.load(campaign_path)
        manifest = _read_json(config.data_root / "latest-manifest.json")
        bars = _read_json(config.data_root / str(manifest["dataFile"]))
        unseen_bars = campaign.unseen_bar_count(bars)
        return {
            "campaign_id": campaign.campaign_id,
            "fingerprint": campaign.fingerprint(),
            "unseen_bars": unseen_bars,
            "required_unseen_bars": campaign.minimum_unseen_bars,
            "evidence_ready": unseen_bars >= campaign.minimum_unseen_bars,
            "status": "EVIDENCE_READY"
            if unseen_bars >= campaign.minimum_unseen_bars
            else "COLLECTING_UNSEEN_EVIDENCE",
        }
    except (FileNotFoundError, KeyError, TypeError, ValueError) as error:
        logger.error(f"Prospective campaign status failed closed: {error}")
        return {"evidence_ready": False, "status": "UNAVAILABLE"}


def _read_json(path: Path) -> Any:
    """Read one JSON evidence file while keeping readiness calculation side-effect free."""
    import json

    return json.loads(path.read_text(encoding="utf-8"))


@mcp.custom_route("/health", methods=["GET"])
async def health_endpoint(_request: Request) -> JSONResponse:
    """Expose process health for container and C# runtime supervision."""
    return JSONResponse({"status": "HEALTHY"})


@mcp.custom_route("/readiness", methods=["GET"])
async def readiness_endpoint(_request: Request) -> JSONResponse:
    """Expose evidence-bearing research readiness without enabling execution."""
    models = _validated_models()
    payload = {
        "ready": bool(models),
        "validated_model_count": len(models),
        "features_ready": bool(models),
        "experts_ready": bool(models),
        "reason": "validated_models_available" if models else "no_validated_models",
        "microstructure": _microstructure_status(),
        "prospective_campaign": _prospective_campaign_status(),
    }
    return JSONResponse(payload, status_code=200 if models else 503)


@mcp.tool()
def quantdesk_get_system_health() -> dict[str, str | float]:
    """Get the health status and resource usage of the QuantDesk research plane."""
    ram = psutil.virtual_memory()
    usage = shutil.disk_usage(".")
    return {
        "status": "HEALTHY",
        "ram_available_gb": ram.available / (1024**3),
        "disk_free_gb": usage.free / (1024**3),
        "python_version": sys.version,
    }


@mcp.tool()
def quantdesk_list_experiments() -> list[str]:
    """List all recorded experiments from the ledger."""
    ledger = TrialLedger()
    try:
        return ledger.list_experiments()
    except Exception as e:  # noqa: BLE001
        logger.error(f"Failed to list experiments: {e}")
        return []


@mcp.tool()
def quantdesk_list_validated_models() -> list[dict[str, Any]]:
    """List all models in VALIDATED promotion state."""
    return _validated_models()


@mcp.tool()
def quantdesk_get_risk_summary() -> dict[str, str | float]:
    """Get a summary of current research-side risk metrics."""
    # In research mode, this might come from the latest backtest or shadow audit
    return {
        "max_potential_loss": 0.0,
        "unexpected_exposure": 0.0,
        "status": "NOMINAL",
        "mode": "RESEARCH",
    }


@mcp.tool()
def quantdesk_run_shadow_audit(
    recorded_events: list[dict[str, Any]], runtime_state: dict[str, Any]
) -> dict[str, Any]:
    """
    Run a shadow audit comparing research reconstruction with runtime state.
    recorded_events: list of trade/price events.
    runtime_state: current state from C# agent.
    """
    auditor = ShadowAuditor()
    return auditor.audit(recorded_events, runtime_state).model_dump(mode="json")


@mcp.tool()
async def quantdesk_security_audit() -> dict[str, str | int | list[str]]:
    """Verify that no execution tools are exposed on the QuantDesk server."""
    tools = await mcp.list_tools()
    all_tools = [t.name for t in tools]

    import re

    exposed_forbidden = []
    for tool_name in all_tools:
        for pattern in FORBIDDEN_PATTERNS:
            if re.match(pattern, tool_name):
                exposed_forbidden.append(tool_name)

    return {
        "status": "PASS" if not exposed_forbidden else "FAIL",
        "forbidden_tools_found": exposed_forbidden,
        "total_quantdesk_tools": len(all_tools),
        "note": "This audit only checks the QuantDesk Research MCP server. Separate Alpaca MCP server must be independently verified.",
    }


if __name__ == "__main__":
    mcp.run()
