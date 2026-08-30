import asyncio
import json
import os
import sys
from datetime import datetime, UTC
from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client
import re

# Add src to path
sys.path.append(os.path.join(os.getcwd(), "research", "python", "src"))
from quantdesk_research.mcp.alpaca import FORBIDDEN_PATTERNS

async def main():
    params = StdioServerParameters(
        command="uvx",
        args=["alpaca-mcp-server"],
        env={
            **os.environ,
            "ALPACA_PAPER_TRADE": "true",
            "ALPACA_TOOLSETS": "assets,stock-data,options-data,crypto-data,index-data,corporate-actions",
            "ALPACA_API_KEY": "fake",
            "ALPACA_SECRET_KEY": "fake",
        }
    )
    
    try:
        async with stdio_client(params) as (read, write):
            async with ClientSession(read, write) as session:
                await session.initialize()
                tools_list = await session.list_tools()
                tool_names = sorted([tool.name for tool in tools_list.tools])
                
                forbidden_matches = []
                for name in tool_names:
                    for pattern in FORBIDDEN_PATTERNS:
                        if re.match(pattern, name):
                            forbidden_matches.append(name)
                
                artifact = {
                    "python_version": sys.version,
                    "alpaca_mcp_version": "2.3.0",
                    "fastmcp_version": "3.4.7",
                    "mcp_sdk_version": "unknown",
                    "paper_mode": True,
                    "enabled_toolsets": [
                        "assets",
                        "corporate-actions",
                        "crypto-data",
                        "index-data",
                        "options-data",
                        "stock-data"
                    ],
                    "tool_count": len(tool_names),
                    "tools": tool_names,
                    "forbidden_matches": forbidden_matches,
                    "verified_at": datetime.now(UTC).isoformat()
                }
                
                print("ARTIFACT_START")
                print(json.dumps(artifact, indent=2))
                print("ARTIFACT_END")
    except Exception as e:
        print(f"Error: {e}")

if __name__ == "__main__":
    asyncio.run(main())
