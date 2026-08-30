# QUANTDESK ALPACA CLI ASSESSMENT

Checked at: `2026-08-30T18:49:24.8077731+02:00` (`Africa/Johannesburg`)

No order, including a paper order, was submitted during this assessment.

## 1. CLI

repository:
`alpacahq/cli`

version:
`0.0.14`

release:
`v0.0.14`

tag ref:
`880c09f34d937fbd4a20d431076de0fcde4968a7`

status:
`ALPHA PREVIEW`

installation:
`PASS`

installation method:
Pinned official GitHub release asset, `cli_0.0.14_windows_amd64.zip`. Go was not
installed, so no Go toolchain or unrelated package was added.

install path:
`C:\Users\Admin\AppData\Local\Programs\AlpacaCli\0.0.14\alpaca.exe`

archive SHA-256:
`7f4838887ac4218c465f3be1423b0cd2fbd6a26323b4ae4de8ec0a09f2bfc107`

binary SHA-256:
`48220cb7ad4674ff64c55264769127f4ff3b7eaa6b1124958b4bb15e09613304`

PATH:
The versioned install directory is present in the user PATH. A new terminal or
Codex restart may be required for inherited process environments to see it.

## 2. PAPER SAFETY

default paper:
`YES`

live env:
Host `ALPACA_LIVE_TRADE` was unset. Every authenticated diagnostic child process
set `ALPACA_LIVE_TRADE=false` explicitly.

live blocked by adapter:
`FAIL` — no CLI adapter was implemented. The existing native C# gateway does
independently restrict its base URL to `https://paper-api.alpaca.markets`.

credentials:
`environment`

The existing `APCA_API_KEY_ID` and `APCA_API_SECRET_KEY` values were copied only
into each child process as `ALPACA_API_KEY` and `ALPACA_SECRET_KEY`. They were not
written to source, profiles, command arguments, or this artifact.

secret logging audit:
`PASS` for the assessment runs. Captures separated stdout and stderr and applied
an additional exact-value redaction before emitting diagnostic text. No saved
CLI profile exists.

## 3. DIAGNOSTICS

doctor:
`PASS` — exit `0`, 2,597 ms; version `0.0.14`; paper profile; paper Trading API
connected; market Data API connected; no saved profile.

account:
`PASS` — exit `0`, 861 ms; valid JSON; status `ACTIVE`.

clock:
`PASS` — exit `0`, 1,228 ms; valid JSON; market reported closed on Sunday,
2026-08-30, with the next open on 2026-08-31.

positions:
`PASS` — exit `0`, 840 ms; valid JSON array; count `0`.

orders:
`PASS` — exit `0`, 877 ms; valid JSON array; open-order count `0`.

## 4. OUTPUT CONTRACT

JSON:
`PASS`

quiet mode:
`PASS`

structured errors:
`PASS`

exit code mapping:
`PASS`

Observed mappings:

- Success: exit `0`.
- Missing asset/API failure: exit `1`, structured stderr with `error`, `code`,
  and `status=404`.
- Deliberately invalid credentials: one request only, exit `2`, structured
  stderr with `status=401` and a hint. Authentication was not retried.

## 5. IDEMPOTENCY

client-order-id:
`PASS` as a CLI capability; the installed binary exposes
`--client-order-id` with a 128-character maximum. QuantDesk CLI enforcement was
not implemented.

retry resolution:
`FAIL` — no CLI adapter or persistent retry-resolution workflow exists.

restart handling:
`FAIL` — no CLI client-order-id provenance store exists across restart.

The current native gateway already submits and queries client order IDs, but a
new logical ID is generated when the operator request omits one. That behavior
is not sufficient proof of restart-safe autonomous retries. The local retrieval
corpus likewise treats a stable deduplication key recorded with the effect as
the reliable boundary (`knowledge-base/sources/external/messaging-delivery-semantics.md`,
lines 79-90).

## 6. EQUITY SUPPORT

asset lookup:
`PASS` — SPY reported `active`, `tradable=true`, and `fractionable=true`.

market data:
`PASS` — five SPY daily bars were returned for 2026-08-24 through 2026-08-29.

stock order dry-run:
`FAIL` — intentionally not run because no equity strategy is qualified. A
synthetic order candidate would violate the required research-before-execution
ordering.

fractional/notional:
`PASS` for current CLI schema and asset capability only. The installed binary
exposes mutually exclusive `--qty` and `--notional`; notional is restricted to
market/day orders. No order serialization was exercised.

market session:
`PASS` as a CLI read capability; `FAIL` as an execution gate because no adapter
was implemented.

## 7. QUANTDESK ADAPTER

implemented:
`NO`

interface:
Existing `IBrokerExecutionGateway`; a duplicate `IPaperBroker` was not added.

paper only:
`FAIL` for a nonexistent CLI adapter. The existing native implementation is
paper-host pinned.

$5 cap:
`FAIL` for the proposed equity experiment. The current paper-order setting
defaults to `$1,000` in `PaperTradingOptions`, `.env.example`, and Compose.
The central `RiskGovernor` has a separate `$5` per-candidate limit, but the
operator `PaperOrderApplicationService` calls the broker directly.

risk bypass possible:
`YES` if the operator order endpoint were misused as autonomous equity
execution. It checks runtime state, account state, buying power, symbol, and the
configured notional cap, but it does not require equity research, committee, or
central-risk approval before calling the broker. This existing operator-only
path must not become the equity automation path.

## 8. NATIVE VS CLI

Native C#:

- effort: `LOWER` — extend the existing broker port with only the required asset,
  clock, notional, and reconciliation behavior.
- failure surface: `LOWER` — one HTTP/JSON stack and no child process or binary
  schema dependency.
- maintainability: `HIGHER` — existing tests already use owned HTTP seams.
- paper safety: `STRONGER TODAY` — the gateway rejects any non-paper Alpaca host.
- time to hackathon execution: `SHORTER AFTER RESEARCH QUALIFICATION`.
- testability: `HIGH`.
- long-term suitability: `PREFERRED`.

CLI:

- effort: `HIGHER` — process runner, timeout/kill behavior, separate stream
  parsing, version support, provenance, redaction, schema tests, and packaging.
- failure surface: `HIGHER` — executable discovery, process lifecycle, exit
  codes, alpha command/schema drift, and CLI-internal retries.
- maintainability: `LOWER` while Alpha Preview.
- paper safety: `ACHIEVABLE` only with a second fail-closed wrapper.
- time to hackathon execution: `NO ADVANTAGE` because native execution already
  exists.
- testability: `MEDIUM` with an owned fake process runner.
- long-term suitability: `DIAGNOSTICS/ORACLE ONLY`.

The local architecture corpus describes ports as contracts implemented by
infrastructure adapters and recommends keeping external calls behind those
ports (`free-library/12-open-book-repos/domain-driven-hexagon/README.md`, lines
291-293). QuantDesk already has that port, so a second broker abstraction would
duplicate the boundary.

recommended hackathon path:
`NATIVE_CSHARP`, but only after the equity research gate passes.

Current selection:

```text
EQUITY_EXECUTION_PATH =
NO_EXECUTION_INTEGRATION_YET
```

## 9. ALPHA-PREVIEW RISK

- command stability: `HIGH RISK`; upstream explicitly permits breaking command
  and flag changes.
- JSON schema stability: `HIGH RISK`; generated OpenAPI changes can alter
  response contracts between releases.
- version pinning: `REQUIRED`; version and both archive/binary hashes are
  recorded above.
- deployment packaging: `UNNECESSARY NOW`; use host-only diagnostics. Do not add
  a helper service.
- Go binary dependency: `MANAGEABLE`; the official prebuilt Windows binary
  avoided a Go runtime/build dependency.
- subprocess overhead: `LOW FOR DIAGNOSTICS`, but needless on an execution hot
  path already served natively.
- failure semantics: `WELL OBSERVED FOR 0/1/2`, but ambiguous submission
  timeouts and internal 429/5xx retries still require durable client-order-id
  reconciliation.

verdict:
`DIAGNOSTICS_ONLY`

## 10. EQUITY RESEARCH STATUS

qualified strategy:
`NO`

strategy:
`NONE`

BASE economics:
`NOT PRODUCED`

confidence:
`NOT PRODUCED`

The repository has an equity data publisher, a generic equity cost model, and
generic strategy/backtest primitives, but no `US_EQUITIES_RESEARCH_001`
qualification artifact proving positive BASE net expectancy, causality,
stability, confidence, and risk gates. The concurrently created untracked
`EquityFeeSchedule.cs` improves cost provenance but does not qualify a strategy
and was preserved untouched.

## 11. EXECUTION AUTHORITY

enabled:
`NO`

reason:
No qualified equity strategy, no CLI adapter, no `$5` equity-specific execution
fence, and no CLI restart-safe retry implementation. CLI availability is not
execution authorization.

## 12. FIRST EQUITY PAPER ORDER

submitted:
`NO`

exact gate:
`US_EQUITIES_RESEARCH_001` has not produced a strategy with positive BASE net
expectancy plus causality, stability, confidence, and risk PASS. Consequently,
no dry-run candidate and no submission were authorized.

## 13. CRYPTO PATH

changed:
`NO`

broken:
`NO CHANGE-INDUCED BREAK`; no crypto source was edited. The Python suite passed
49 tests. C# verification was blocked because this host has no installed SDK
matching `global.json` (`10.0.400`).

## 14. VALIDATED PATH

changed:
`NO`

## 15. FINAL RECOMMENDATION

Retain Alpaca CLI `0.0.14` as a pinned, host-only paper diagnostics and
reconciliation oracle. Do not implement CLI execution. After equity research
qualifies, extend the existing native C# `IBrokerExecutionGateway` and route
autonomous equity orders through the same research, committee, risk,
reservation, execution, and reconciliation authority used by QuantDesk.

Hackathon:
`NATIVE_CSHARP`, after qualification.

Production:
`NATIVE_CSHARP`.

Do not package the CLI into the API container or add a helper service unless a
future, evidence-backed capability gap cannot be closed safely in the native
gateway.

Verification evidence:

- CLI read-only diagnostics and error probes: `PASS`.
- Python: `49 passed`, one existing rank-deficient HAR warning.
- C#: `BLOCKED`, no .NET SDK `10.0.400` installed.
- Paper orders submitted: `0`.

## 16. SINGLE NEXT ACTION

Complete `US_EQUITIES_RESEARCH_001` and publish its immutable qualification
artifact with positive BASE net expectancy and causality, stability,
confidence, and risk PASS. Until that artifact exists, keep equity execution
disabled.
