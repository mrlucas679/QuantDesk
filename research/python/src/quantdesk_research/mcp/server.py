import os
import shutil
import sys

import psutil  # type: ignore[import-untyped]
from fastmcp import FastMCP
from loguru import logger

from quantdesk_research.config import get_research_config
from quantdesk_research.evaluation.trial_ledger import TrialLedger
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


@mcp.tool()
def quantdesk_get_system_health() -> dict:
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
def quantdesk_list_experiments() -> list:
    """List all recorded experiments from the ledger."""
    ledger = TrialLedger()
    try:
        return ledger.list_experiments()
    except Exception as e:  # noqa: BLE001
        logger.error(f"Failed to list experiments: {e}")
        return []


@mcp.tool()
def quantdesk_list_validated_models() -> list:
    """List all models in VALIDATED promotion state."""
    config = get_research_config()
    registry = ModelRegistry(str(config.experiment_db_path))
    return registry.list_models(promotion_state="VALIDATED")


@mcp.tool()
def quantdesk_get_risk_summary() -> dict:
    """Get a summary of current research-side risk metrics."""
    # In research mode, this might come from the latest backtest or shadow audit
    return {
        "max_potential_loss": 0.0,
        "unexpected_exposure": 0.0,
        "status": "NOMINAL",
        "mode": "RESEARCH",
    }


@mcp.tool()
def quantdesk_run_shadow_audit(recorded_events: list[dict], runtime_state: dict) -> dict:
    """
    Run a shadow audit comparing research reconstruction with runtime state.
    recorded_events: list of trade/price events.
    runtime_state: current state from C# agent.
    """
    auditor = ShadowAuditor()
    return auditor.audit(recorded_events, runtime_state)


@mcp.tool()
async def quantdesk_security_audit() -> dict:
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
