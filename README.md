# QuantDesk

QuantDesk is a paper-trading decision system. The production trading runtime is C#/.NET; Python is reserved for offline research and model development.

## Local configuration

Set these values in your user environment or a local secret store. Never commit them.

```powershell
$env:APCA_API_BASE_URL = "https://paper-api.alpaca.markets"
$env:APCA_API_KEY_ID = "your-paper-key"
$env:APCA_API_SECRET_KEY = "your-paper-secret"
```

Build and test without starting the API:

```powershell
dotnet restore QuantDesk.slnx
dotnet build QuantDesk.slnx --no-restore
dotnet test QuantDesk.slnx --no-build
```

Build the C# API container without starting it:

```powershell
docker compose build quantdesk-api
```

Run it only when explicitly needed, with paper credentials injected by the shell:

```powershell
docker compose up quantdesk-api
```

Private specifications and Python research are excluded from the Docker build context.

## MCP Servers

QuantDesk Research uses two distinct MCP (Model Context Protocol) servers to maintain clear trust boundaries and security invariants.

### 1. QuantDesk Research MCP Server
**Purpose:** Internal research, experiment management, and model auditing.
- Shadow Audit capabilities (comparing research vs. runtime)
- Experiment ledger access
- Backtesting model evidence
- QuantDesk state reads

**Running:**
```powershell
cd research/python
uv run src/quantdesk_research/mcp/server.py
```

### 2. Alpaca Research MCP Server
**Purpose:** External market data and informational queries.
- Market data (Stock, Options, Crypto, Indices)
- Asset information and calendars
- Corporate action information

**Running via uvx:**
```powershell
# Enforce security boundary with explicit safe toolsets
$env:ALPACA_PAPER_TRADE = "true"
$env:ALPACA_TOOLSETS = "assets,stock-data,options-data,index-data,crypto-data,corporate-actions"
uvx alpaca-mcp-server
```

### 3. C# Alpaca Trading API (Non-MCP)
**Purpose:** Authoritative paper execution and order management.
- Order placement, replacement, and cancellation
- Broker state reconciliation
- Atomic reservations and Risk Governor enforcement
- **This component is the ONLY one with broker execution authority.**

### Security Boundary & Architecture

- **Execution Authority**: The Python/MCP plane has **ZERO** authority to place, cancel, or modify orders. All trading execution must flow through the C# Risk Governor.
- **DETERMINISTIC FINANCIAL BOUNDARY**: No Python/MCP execution shortcut may exist. C# remains the sole execution authority.
- **Separate Servers**: QuantDesk and Alpaca are run as separate MCP servers to ensure separate trust boundaries and dependency isolation.
- **Fail Closed**: If Alpaca integration is enabled in QuantDesk but toolsets are not configured, it will fail validation.
- **Paper Mode**: `ALPACA_PAPER_TRADE=true` is mandatory for the research environment.

## Sanitized Configuration Examples

### Claude Code / Codex Configuration

```json
{
  "mcpServers": {
    "quantdesk": {
      "command": "uv",
      "args": ["--directory", "research/python", "run", "src/quantdesk_research/mcp/server.py"]
    },
    "alpaca": {
      "command": "uvx",
      "args": ["alpaca-mcp-server"],
      "env": {
        "ALPACA_API_KEY": "YOUR_PAPER_KEY_HERE",
        "ALPACA_SECRET_KEY": "YOUR_PAPER_SECRET_HERE",
        "ALPACA_PAPER_TRADE": "true",
        "ALPACA_TOOLSETS": "assets,stock-data,options-data,index-data,crypto-data,corporate-actions"
      }
    }
  }
}
```

### Configuration

Alpaca credentials must be set in the environment:
- `APCA_API_KEY_ID`: Your Alpaca API Key.
- `APCA_API_SECRET_KEY`: Your Alpaca API Secret.

These are automatically bridged if using the QuantDesk configuration adapter.
