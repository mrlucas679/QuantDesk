# QuantDesk

QuantDesk is a paper-trading decision system. The production trading runtime and sole execution authority are C#/.NET; Python provides offline research, validation, and read-only MCP tools.

## Local configuration

Set these values in your user environment or a local secret store. Never commit them.

```powershell
$env:APCA_API_BASE_URL = "https://paper-api.alpaca.markets"
$env:APCA_API_KEY_ID = "your-paper-key"
$env:APCA_API_SECRET_KEY = "your-paper-secret"
$env:QUANTDESK_OPERATOR_KEY = "a-long-random-local-operator-key"
$env:QUANTDESK_SYMBOLS = "SPY"
$env:QUANTDESK_MAX_PAPER_ORDER_NOTIONAL = "1000"
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

Private specifications and Python research dependencies are excluded from the production Docker image.

## First paper trade

Start the API with Docker Compose, then wait until broker reconciliation has completed:

```powershell
docker compose up -d --build quantdesk-api
Invoke-RestMethod http://localhost:8080/ready
```

Submit a small limit order. Use a deliberately conservative limit price for the first connectivity test so it does not fill unexpectedly:

```powershell
$headers = @{ "X-QuantDesk-Operator-Key" = $env:QUANTDESK_OPERATOR_KEY }
$order = @{
  symbol = "SPY"
  side = "buy"
  quantity = 1
  limitPrice = 1.00
  clientOrderId = "quantdesk-first-paper-trade"
} | ConvertTo-Json

$submitted = Invoke-RestMethod `
  -Method Post `
  -Uri http://localhost:8080/api/paper/orders `
  -Headers $headers `
  -ContentType "application/json" `
  -Body $order

$submitted
```

Cancel the test order using the returned broker order ID:

```powershell
Invoke-RestMethod `
  -Method Delete `
  -Uri "http://localhost:8080/api/paper/orders/$($submitted.brokerOrderId)" `
  -Headers $headers
```

The API accepts paper limit orders only, restricts symbols through `QUANTDESK_SYMBOLS`, rejects orders above `QUANTDESK_MAX_PAPER_ORDER_NOTIONAL`, checks paper-account status and buying power, and requires the operator key. It will not become ready when Alpaca reconciliation fails.

## Autonomous paper execution canary

The API can run a bounded, one-cycle autonomous paper trade without an operator
submitting an order. It reads Alpaca's latest BTC/USD quote, buys approximately
$20 of paper BTC, reconciles the actual fee-adjusted position quantity, closes
that exact quantity, and verifies that the account is flat.

Enable it only in the paper environment:

```powershell
$env:QUANTDESK_SYMBOLS = "SPY,BTC/USD"
$env:QUANTDESK_MAX_PAPER_ORDER_NOTIONAL = "25"
$env:QUANTDESK_AUTONOMOUS_ENABLED = "true"
$env:QUANTDESK_AUTONOMOUS_SYMBOL = "BTC/USD"
$env:QUANTDESK_AUTONOMOUS_ORDER_NOTIONAL = "20"
$env:QUANTDESK_AUTONOMOUS_CYCLE_INTERVAL_SECONDS = "300"
docker compose up -d --build quantdesk-api
```

Its observable state is available at `GET /api/autonomous/status`. Every
successful cycle ends in `completed_flat`, waits for the configured interval,
then repeats while the container remains healthy. The feature is disabled by
default and must be explicitly enabled for paper-only autonomous testing.

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
