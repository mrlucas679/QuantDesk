import re

import pytest

from quantdesk_research.mcp.alpaca import FORBIDDEN_PATTERNS
from quantdesk_research.mcp.server import mcp


@pytest.mark.asyncio
async def test_quantdesk_mcp_security_invariants():
    """
    Criterion 10: QuantDesk MCP security must remain green.
    Verify that no dangerous tools are exposed on the QuantDesk Research MCP server.
    """
    tools = await mcp.list_tools()
    all_tools = [t.name for t in tools]

    # Specific forbidden keywords from Criterion 10
    CRITERION_10_FORBIDDEN = {
        "place_order",
        "buy",
        "sell",
        "cancel_order",
        "close_position",
        "create_reservation",
        "change_hard_risk",
        "disable_risk",
        "activate_policy",
        "read_secret",
    }

    exposed_forbidden = []
    for tool_name in all_tools:
        # Check against regex patterns
        for pattern in FORBIDDEN_PATTERNS:
            if re.match(pattern, tool_name):
                exposed_forbidden.append(tool_name)

        # Check against exact Criterion 10 names
        if tool_name in CRITERION_10_FORBIDDEN:
            exposed_forbidden.append(tool_name)

    assert not exposed_forbidden, f"QuantDesk MCP exposes forbidden tools: {exposed_forbidden}"
    print(f"QuantDesk MCP security check passed. {len(all_tools)} tools verified.")


if __name__ == "__main__":
    import asyncio

    asyncio.run(test_quantdesk_mcp_security_invariants())
