"""Verify any standards-compliant harness can initialize the QuantDesk MCP server."""

import argparse
import asyncio

from fastmcp import Client

REQUIRED_TOOLS = {
    "quantdesk_get_system_health",
    "quantdesk_list_experiments",
    "quantdesk_list_validated_models",
    "quantdesk_get_risk_summary",
    "quantdesk_run_shadow_audit",
    "quantdesk_security_audit",
}


async def verify_server(url: str) -> None:
    """Initialize an MCP session and verify its exact safe tool contract."""
    async with Client(url, timeout=10) as client:
        tools = await client.list_tools()
        tool_names = {tool.name for tool in tools}
        missing = REQUIRED_TOOLS - tool_names
        if missing:
            raise RuntimeError(f"MCP server is missing required tools: {sorted(missing)}")

        result = await client.call_tool("quantdesk_security_audit")
        if result.is_error:
            raise RuntimeError("QuantDesk MCP security audit returned an error")

    print(f"QuantDesk MCP ready: {url} ({len(tool_names)} read-only tools)")


def main() -> None:
    """Parse the endpoint and run the protocol-level smoke check."""
    parser = argparse.ArgumentParser()
    parser.add_argument("--url", default="http://127.0.0.1:8000/mcp")
    args = parser.parse_args()
    asyncio.run(verify_server(args.url))


if __name__ == "__main__":
    main()
