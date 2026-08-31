# QuantDesk Agent Operating Contract

This file is the repository-level source of truth for humans and coding agents working on QuantDesk. Read it before changing code, starting services, or acting on a request to trade. Keep it aligned with the code; never turn an aspiration into a claimed capability.

## Mission and current scope

QuantDesk is a PAPER-only algorithmic trading system. The C#/.NET application is the sole execution authority. Python and MCP provide research, validation, experiments, and market-data reads. They never place, replace, cancel, close, or otherwise mutate broker orders or positions.

The immediate product goal is autonomous PAPER trading through the complete QuantDesk application. When the user says to trade, that is authorization to use the application workflow described below. It is not authorization to bypass admission, research, risk, reservation, execution, recovery, exit, or reconciliation controls.

The Alpaca AI Trading Agents Hackathon deadline recorded for this repository is 2026-09-04 17:00 Africa/Johannesburg. Submission requirements include an autonomous agent using Alpaca Trading API plus MCP or CLI, options incorporated into every submitted strategy, a fresh dedicated judging PAPER account with the required balance, the PAPER account ID, and a one-page write-up. Re-check the official rules before submission because external requirements can change.

## Non-negotiable execution boundary

All user-authorized strategy trades must follow this route:

1. QuantDesk runtime preflight and broker/internal reconciliation.
2. Research evidence and strategy qualification for the selected autonomous mode.
3. Deterministic expert, cost, actionability, and `RiskGovernor` decisions.
4. Risk and capital reservation before broker submission.
5. `ExecutionWorker` through `IBrokerExecutionGateway` and `AlpacaTradingGateway`.
6. Broker fill tracking and internal portfolio attribution.
7. Application-owned exit management.
8. Final broker/internal reconciliation and proof of zero unintended exposure.

Never submit a user-requested trade with direct Alpaca HTTP calls, the Alpaca MCP server, the external Alpaca CLI, `paper-order-smoke`, curl, PowerShell web requests to Alpaca, or an ad hoc script. Do not use an operator endpoint merely to imitate autonomous strategy approval. Capability is not authorization.

Only `https://paper-api.alpaca.markets` is permitted. `AlpacaOptions` and `AlpacaTradingGateway` reject any other trading host. Never weaken that check, introduce live credentials, or add a live-money fallback.

If a broker POST has an ambiguous outcome, look up the existing order by its original client-order ID before doing anything else. Never generate a replacement client ID to retry an uncertain submission.

## Verified integration map

- C# execution API: `src/QuantDesk.Api` wires the runtime, hosted workers, operator endpoints, broker gateway, market data, readiness, autonomous trading, and diagnostic recovery.
- Alpaca adapter: `src/QuantDesk.Alpaca/Trading/AlpacaTradingGateway.cs` implements PAPER account/asset reads, submit, lookup by client-order ID, open orders, positions, cancel, replace, and close-position operations.
- Deterministic runtime: `src/QuantDesk.Runtime` owns execution intents, reservations, risk, portfolio accounting, exits, reconciliation, persistence, and recovery primitives.
- QuantDesk Research MCP: `research/python/src/quantdesk_research/mcp/server.py` exposes research health, experiments, validated models, risk summaries, and shadow audits. It exposes no execution tools.
- Alpaca Research MCP: configured as a separate server with the allowlisted toolsets `assets,stock-data,options-data,index-data,crypto-data,corporate-actions`. `ALPACA_PAPER_TRADE=true` and a non-empty safe allowlist are mandatory.
- External Alpaca CLI 0.0.14: pinned diagnostic/reconciliation oracle only. It is not an execution stack.
- QuantDesk CLI: `paper-order-smoke` is a bounded connectivity test, not the route for a user-authorized strategy trade.

## Current implemented trading lanes

### Durable BTC/USD diagnostic

`CryptoDiagnosticExecutionService` is an infrastructure diagnostic, not strategy authorization. Its admission intentionally ignores features, experts, strategy qualification, momentum alignment, and `PAPER_ELIGIBLE`. It requires a positively verified PAPER endpoint, healthy authenticated account, tradable BTC/USD, the diagnostic risk envelope, durable persistence, recovery availability, and clean broker/internal reconciliation.

It persists deterministic entry and exit reservations before broker POSTs, recovers ambiguous submissions by client-order ID, tracks accepted/partial/filled/terminal states, holds exactly two minutes from the final entry fill, exits from broker position truth, resumes all nonterminal states after restart, and completes only after final zero/zero reconciliation. Emergency flatten is diagnostic-scoped, PAPER-protected, and idempotent.

The completed proof `CRYPTO-DIAGNOSTIC-2026-08-31-001` demonstrated one PAPER entry, one worker-owned exit, deterministic recovery, and flat reconciliation. Its durable exit was scheduled exactly two minutes after final entry fill, but a runtime repair deployment delayed the actual trigger; do not describe that observed hold as exactly two minutes. It does not authorize a second diagnostic or an autonomous strategy trade.

### Autonomous spot-crypto strategy

`AutonomousPaperTradingService` and `AutonomousDecisionPipeline` currently implement autonomous spot-crypto evaluation and execution. The service checks broker health and a clean account, obtains market evidence, runs the research/committee/compiler/cost/actionability/risk path, reserves capital and risk, submits through `ExecutionWorker`, manages the position through `ExitEngine`, and reconciles after exit.

Modes are `Disabled`, `ExperimentalPaper`, and `ValidatedPaper`. `ValidatedPaper` requires a compatible verified directional forecast and full runtime readiness. `ExperimentalPaper` requires a complete preregistered authorization and its sanity checks, but deliberately relaxes some research-readiness gates. Do not silently switch modes to make a trade pass.

The autonomous lane is disabled unless both its mode and `QUANTDESK_AUTONOMOUS_ENABLED` enable it. Do not infer effective state from documentation: inspect the actual container environment and `GET /api/autonomous/status`. Compose defaults autonomous execution to disabled and a $20 notional, but effective runtime configuration remains authoritative.

The current autonomous service uses generated client IDs and in-memory reservation/portfolio objects. Its restart guarantees are not equivalent to the durable diagnostic lifecycle. Do not claim durable autonomous restart recovery unless the implementation and tests prove it.

### Options status

The repository contains option contracts, Black-Scholes calculations, chain validation, defined-risk payoff logic, research code/tests, options market-data access through the read-only Alpaca MCP, and an Alpaca account capability probe that checks `options_trading_level`.

The current `AlpacaTradingGateway` submits a single generic order payload, and the autonomous decision pipeline is explicitly spot crypto. No verified application-owned autonomous multi-leg options execution path is present. Therefore, do not claim that QuantDesk currently meets the hackathon's options-in-every-strategy requirement and do not send an options trade until multi-leg construction, risk, reservation, submission, lifecycle, recovery, and reconciliation are implemented and tested through the C# application boundary.

## What to do when the user says "trade"

The user's instruction supplies the human authorization to begin a bounded PAPER run; the system still decides whether an admissible trade exists. Do not promise that an order will be placed. Abstention or entry halt is a valid and often required result.

Before enabling or resuming a run, retrieve evidence from the actual runtime:

- API and all required worker/container health.
- exact trading endpoint is Alpaca PAPER.
- authenticated account is active, unblocked, and has the required crypto/options permission for the intended instrument.
- intended asset/contract is active and tradable.
- effective mode, symbol, notional, maximum exposure, fill timeout, exit policy, and experiment authorization.
- fresh research artifact and market data required by the selected mode.
- risk envelope is green.
- durable stores required by the chosen lifecycle are writable and recoverable.
- no unexplained relevant position or unresolved order.
- broker/internal reconciliation passes.
- automatic exit and relevant recovery worker are registered and active.

Then use the existing application control surface and observe its state. Do not create a second experiment or order because progress is slow or a response is ambiguous. Let the worker own the lifecycle. During a durable hold, read status only; do not use a client-side delay as lifecycle ownership and do not require another user message.

Stop fail-closed on live endpoint configuration, unhealthy account, missing permission, stale/invalid evidence, unresolved exposure/order, reconciliation failure, missing recovery/exit ownership, failed reservation, risk rejection, or ambiguous state that cannot be recovered safely. Identify the lowest-level failure without exposing credentials or raw secrets.

After completion, report the experiment/run ID; entry and exit client/broker IDs; fill timestamps, prices, and quantities; hold/exit timing; final broker and internal quantities; reconciliation; PAPER P&L; data age; broker RTT; submit-to-fill and exit latency; and full round-trip duration. Evidence must come from persisted application and broker truth.

## Runtime controls and observations

Primary read endpoints:

- `GET /health`
- `GET /ready`
- `GET /api/system/status`
- `GET /api/system/readiness`
- `GET /api/system/capabilities`
- `GET /api/autonomous/status`
- `GET /api/research/status`
- `GET /api/research/microstructure-status`
- authenticated `GET /api/diagnostics/recovery`
- authenticated `GET /api/diagnostics/{experimentId}`

Operator mutations require `X-QuantDesk-Operator-Key` and fail closed when the key is blank. Never print the key. Diagnostic start, system halt/risk-reduction, and generic paper-order endpoints are operator controls; their existence does not confer strategy qualification.

Required secrets/configuration belong in the environment or local secret store. Never commit `.env` files, API keys, account identifiers that are meant to remain private, or raw broker responses. Keep logs structured and sanitized.

## Build, test, and runtime verification

Use PowerShell from the repository root:

```powershell
dotnet restore QuantDesk.slnx
dotnet build QuantDesk.slnx --no-restore
dotnet test QuantDesk.slnx --no-build
docker compose build
```

Starting local services can initiate autonomous behavior under the effective Compose defaults. Do not run `docker compose up` casually. Inspect environment and authorization first, and start services only when the user has asked for the runtime to be used. Build/test commands are safe verification; a green build is not broker/runtime proof.

For an authorized runtime session, use `docker compose ps`, container health, application readiness/status endpoints, sanitized logs, persisted state, and broker reconciliation. Do not rely on source inspection or unit tests alone to claim a live trade outcome.

## Engineering and repository discipline

Before non-trivial implementation or review, read the shared standards at `C:\Users\Admin\Downloads\Books-master\Books-master\CODING_STANDARDS.md`. Project-specific rules in this file override general defaults where they intentionally differ. Use the engineering library retrieval tools for covered engineering claims; use current official documentation for gaps such as Docker, observability, or other fast-moving tooling.

Preserve unrelated user changes. Inspect status, branch, remote, and diff before committing. Never push directly to `main`; use a `codex/` branch for new agent-created branches unless the user specifies another branch. Do not commit or push unless requested. Never rewrite broker evidence or durable lifecycle records to make a result look successful.

Use `rg`/`rg --files` for discovery and `apply_patch` for manual file edits. Add tests for logic-heavy changes, especially admission, money/risk boundaries, idempotency, ambiguous broker outcomes, recovery, and reconciliation. A runtime defect may justify an architecture change only when the defect is demonstrated and the smallest safe correction is verified.

## Documentation and evidence pointers

- `README.md`: setup, diagnostic operation, autonomous configuration, and MCP boundaries.
- `docs/18_EXECUTION_AND_RECONCILIATION.md`: execution and reconciliation design.
- `docs/14_STRATEGY_COMPILER.md`: strategy compiler design and options extension intent.
- `audit/QUANTDESK_ALPACA_CLI_ASSESSMENT_2026-08-30.md`: CLI scope and provenance.
- `audit/PYTHON_TRACEABILITY_MATRIX.md`: Python implementation/evidence traceability; verify it against current code before relying on potentially stale status entries.
- `research.md`: current experiment/hypothesis evidence referenced by the default experimental configuration.

When code, configuration, tests, runtime evidence, and documentation disagree, report the disagreement and use the most direct current evidence. Update this file when a verified capability or invariant changes.
