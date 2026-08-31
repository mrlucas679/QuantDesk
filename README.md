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
$env:QUANTDESK_DIAGNOSTIC_MAX_NOTIONAL = "10"
```

Build and test without starting the API:

```powershell
dotnet restore QuantDesk.slnx
dotnet build QuantDesk.slnx --no-restore
dotnet test QuantDesk.slnx --no-build
```

Build the complete containerized runtime without starting it:

```powershell
docker compose build
```

Run the C# execution API, Python research MCP, and research-validation worker.
Datasets, the SQLite experiment ledger, and artifacts persist only in named Docker
volumes; none are mounted from the host:

```powershell
docker compose up -d --build
docker compose ps
```

Private specifications and credentials are excluded from every production image.

## Bounded BTC/USD paper diagnostic

The first broker-path proof is a dedicated diagnostic lifecycle, not strategy
authorization. It requires PAPER endpoint verification, account and asset
health, a clean BTC/USD broker position/order reconciliation, durable storage,
and the recovery worker. It deliberately ignores feature, expert, momentum,
and strategy-qualification readiness.

Start the complete Compose runtime with autonomous strategy execution disabled:

```powershell
$env:QUANTDESK_SYMBOLS = "SPY,BTC/USD"
$env:QUANTDESK_DIAGNOSTIC_MAX_NOTIONAL = "10"
$env:QUANTDESK_AUTONOMOUS_ENABLED = "false"
docker compose up -d --build
$readiness = Invoke-RestMethod http://localhost:8080/api/system/readiness
$readiness.infrastructureExecutionReady
```

Verify that recovery is active, then create or resume one deterministic
diagnostic ID. The operator key must be configured outside the repository.

```powershell
$headers = @{ "X-QuantDesk-Operator-Key" = $env:QUANTDESK_OPERATOR_KEY }
Invoke-RestMethod `
  -Uri http://localhost:8080/api/diagnostics/recovery `
  -Headers $headers

$experimentId = "CRYPTO-DIAGNOSTIC-YYYY-MM-DD-001"
Invoke-RestMethod `
  -Method Post `
  -Uri "http://localhost:8080/api/diagnostics/$experimentId/start" `
  -Headers $headers
```

Do not send another start request during the hold. Polling is read-only; the
registered worker owns the durable two-minute hold, exit, and reconciliation:

```powershell
Invoke-RestMethod `
  -Uri "http://localhost:8080/api/diagnostics/$experimentId" `
  -Headers $headers
```

`Complete` is persisted only after Alpaca reports no unresolved diagnostic
orders, broker BTC/USD exposure is zero, internal diagnostic exposure is zero,
and broker/internal reconciliation passes. Alpaca rejected a $5 BTC/USD order
as below its $10 minimum on 2026-08-31, so the checked-in diagnostic default is
$10. Reconfirm broker limits before changing it.

The current end-to-end proof, `CRYPTO-DIAGNOSTIC-2026-08-31-001`, completed on
2026-08-31 with one PAPER entry, one worker-owned PAPER exit, and final zero/zero
reconciliation. The exit was durably scheduled at final entry fill plus exactly
two minutes; a repair deployment delayed the observed trigger, so this run is
not evidence of exact wall-clock trigger latency. It proves the broker execution
and recovery lane only and does not authorize autonomous strategy entries.

## Autonomous paper execution

The API can evaluate paper opportunities without an operator submitting an
order. It will remain entry-halted until the research plane has a validated
model, fresh features/experts, broker reconciliation, market data, risk, and
execution gates. A timer alone never authorizes a trade.

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
approved paper trade is reconciled against the actual fill and must satisfy
the research edge, spread, slippage, and fee gates. The feature is disabled by
default and is paper-only.

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
