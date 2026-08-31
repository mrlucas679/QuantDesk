# QUANTDESK ALPACA CLI ASSESSMENT

Checked at: `2026-08-30T22:25:34.5065575+02:00` (`Africa/Johannesburg`)

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

upstream evidence:
`https://github.com/alpacahq/cli` and
`https://github.com/alpacahq/cli/releases/tag/v0.0.14`

PATH:
The versioned install directory is present in the user PATH. A new terminal or
Codex restart is required for this already-running process to see it through
`Get-Command alpaca`; all verification used the exact absolute binary path.

installer residue:
The verified 3.85 MB release archive remains at
`C:\Users\Admin\AppData\Local\Temp\quantdesk-alpaca-v0.0.14-01a05388\cli_0.0.14_windows_amd64.zip`.
Automated removal was denied by the environment's destructive-action guard; the
installed binary is independent of this archive.

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
`PASS` — exit `0`, 2,132 ms; version `0.0.14`; paper profile; paper Trading API
connected; market Data API connected; no saved profile.

account:
`PASS` — exit `0`, 817 ms; valid JSON; status `ACTIVE`.

clock:
`PASS` — exit `0`, 778 ms; valid JSON; market reported closed on Sunday,
2026-08-30, with the next open on 2026-08-31.

positions:
`PASS` — exit `0`, 787 ms; valid JSON array; count `0`.

orders:
`PASS` — exit `0`, 853 ms; valid JSON array; open-order count `0`.

sanitized command provenance:
`audit/alpaca-cli-provenance-2026-08-30.json` records the final read-only pass
with version, command family, timestamps, exit codes, durations, paper mode, and
response hashes. It stores neither raw responses nor credential values.

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
`PASS` — the diagnostic returned five SPY daily bars. The completed research
corpus then locked Alpaca SIP/all-adjusted history for SPY, QQQ, IWM, and DIA:
1,965 daily bars per symbol plus 95,011 / 95,489 / 93,053 / 68,895 five-minute
bars respectively, each with a SHA-256 manifest. After regular-session and
completeness filtering, SPY / QQQ / IWM / DIA contributed 498 / 497 / 496 / 495
complete 78-bar sessions.

stock order dry-run:
`NOT RUN — RESEARCH GATE` — intentionally not run because no equity strategy
qualified. Constructing a synthetic order candidate would violate the required
research-before-execution ordering. This is a gate outcome, not evidence of a
CLI dry-run defect.

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

adapter test suite:
`NOT APPLICABLE` — the CLI execution adapter was rejected, so no generic process
runner or dormant submit path was added merely to satisfy test names. The
paper/live rejection, timeout, redaction, exit-code, malformed JSON,
idempotency/restart, market session, `$5`, asset, unavailable binary, and version
tests remain mandatory if this decision is ever reversed. The real paper
integration diagnostics covered account, clock, asset lookup, and data; dry-run
remained behind the failed research gate.

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

Score (`1=weak`, `5=strong`; for effort and failure surface, higher means less):

| Criterion | Native C# | Alpaca CLI |
|---|---:|---:|
| Implementation effort | 4 | 2 |
| Failure surface | 4 | 2 |
| Maintainability | 4 | 2 |
| Paper safety | 4 | 3 |
| Time to hackathon execution | 4 | 2 |
| Testability | 5 | 3 |
| Long-term suitability | 5 | 2 |

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
`ACCEPTABLE_FOR_DIAGNOSTICS_ONLY`

## 10. EQUITY RESEARCH STATUS

qualified strategy:
`NO`

strategy:
`NONE`

BASE economics:
`FAIL` — every one of the 20 preregistered candidates had negative expectancy
after the 25 bps BASE round trip. The best confidence-adjusted candidate,
`daily-monday`, averaged `-10.332362 bps/trade` over 92 validation trades.

cost model:
`BASE=25 bps`, `STRESS=35 bps`, `SEVERE=50 bps` per round trip. BASE assigns
20 bps to spread/slippage and 5 bps to regulatory-fee/rounding uncertainty. It
uses a best-case zero commission because the actual account agreement was not
available; any account-specific commission only makes the negative result
worse, so this omission cannot turn the rejection into a false pass.
Fee provenance was checked against
`https://alpaca.markets/support/regulatory-fees` and Alpaca's current disclosure
library rather than inferred from the CLI.

confidence:
`FAIL` — the best one-sided lower confidence bound was `-28.785644 bps/trade`
after the 20-trial Bonferroni correction (`alpha=0.0025`). Concurrent ETF
signals were equal-weighted into one portfolio observation per session rather
than counted as independent trades.

causality:
`PASS` — features are shifted to observations completed before entry, intraday
entries occur only after their measurement windows, incomplete regular sessions
are excluded, and future-bar mutation tests passed.

stability:
`NOT ELIGIBLE` — no validation candidate reached positive BASE economics or a
positive corrected confidence bound, so the final holdout remained unopened.

risk:
`NOT ELIGIBLE FOR PROMOTION` — drawdown and worst-trade evidence was recorded for
all candidates, but economics and confidence failed before the risk-promotion
gate.

`US_EQUITIES_RESEARCH_001` exhausted all 20 iterations. Its machine decision is
`qualified=false`, `execution_authority=NONE`, and `holdout_evaluated=false`.
The immutable research data and generated evidence are under
`research/python/data/US_EQUITIES_RESEARCH_001` and
`research/python/artifacts/US_EQUITIES_RESEARCH_001` (both intentionally ignored
because datasets and run artifacts are local evidence rather than source).

## 11. EXECUTION AUTHORITY

enabled:
`NO`

reason:
`US_EQUITIES_RESEARCH_001` completed with no validation pass: BASE economics and
corrected confidence both failed. There is also no CLI adapter, no `$5`
equity-specific execution fence, and no CLI restart-safe retry implementation.
CLI availability is not execution authorization.

## 12. FIRST EQUITY PAPER ORDER

submitted:
`NO`

exact gate:
`US_EQUITIES_RESEARCH_001` produced no strategy with positive BASE net
expectancy. The best adjusted lower bound was also negative. Consequently,
stability/risk promotion, exact order construction, CLI dry-run, and submission
were not authorized.

## 13. CRYPTO PATH

changed:
`YES — NATIVE C# DIAGNOSTIC LANE ONLY`

broken:
`NO`. Stages 1-5 added a bounded native `DiagnosticExecution` lifecycle without
authorizing an autonomous strategy. The runtime now requires verified PAPER
infrastructure, deterministic durable reservations, broker-order recovery, an
exact persisted two-minute hold deadline, automatic exit, restart recovery,
strict final reconciliation, and idempotent emergency flattening.

`CRYPTO-DIAGNOSTIC-2026-08-30-001` created exactly one $10 BTC/USD PAPER entry
and one automatic exit on 2026-08-31. Alpaca and internal quantities finished at
zero with reconciliation `Flat`. The diagnostic found and corrected BTC/USD
instrument-slot resolution, Alpaca's observed $10 crypto minimum, and
fee-adjusted internal flatten accounting. These changes do not promote the
equity research campaign or the autonomous crypto strategy.

## 14. VALIDATED PATH

changed:
`NO`

## 15. FINAL RECOMMENDATION

Retain Alpaca CLI `0.0.14` as a pinned, host-only paper diagnostics and
reconciliation oracle. Do not implement CLI execution. After equity research
eventually qualifies, extend the existing native C# `IBrokerExecutionGateway` and route
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
- Equity campaign: `20/20` iterations completed; qualification `FAIL`; holdout
  unopened; execution authority `NONE`.
- Python: `59 passed`; Ruff clean; strict mypy clean; one existing
  rank-deficient HAR warning.
- C#: the complete .NET solution passed `165` tests with zero failures. The
  production API Docker publish/build also passed.
- Existing native-vs-CLI oracle: both observed zero open orders and zero
  positions; native paper preflight and CLI account checks both reported a
  healthy active paper account. QuantDesk's native client has no clock method,
  so clock comparison is CLI-only rather than a false match.
- Paper orders created by the bounded diagnostic: `2` (`1` entry, `1` exit).
  Rejected pre-order validation attempts created no Alpaca order and reused the
  same deterministic experiment/client ID after broker lookup confirmed absence.

## 16. SINGLE NEXT ACTION

Preregister `US_EQUITIES_RESEARCH_002` around lower-turnover, multi-session
equity hypotheses and a new untouched holdout; keep both CLI and native equity
submission disabled until a candidate proves positive BASE economics,
confidence, stability, and risk.
