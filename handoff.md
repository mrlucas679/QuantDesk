# QuantDesk Continuation Handoff

Last refreshed: 2026-08-31, Africa/Johannesburg (verification + option-data continuation)

## Goal

Improve QuantDesk until at least one strategy genuinely qualifies under unchanged PAPER-only, evidence, after-cost, risk, recovery, and reconciliation safeguards. Then execute and complete exactly one bounded autonomous Alpaca PAPER trade through the QuantDesk application without bypassing its controls.

The goal is **not complete**. No strategy currently qualifies, no autonomous strategy order has been submitted, and autonomous execution remains disabled.

## GO-LIVE RUNBOOK — 2026-09-01

The options path is complete and automatic. Everything below the credential step is built, wired,
and tested. **One blocker remains and only you can remove it: no session has ever held Alpaca
credentials, so nothing here has contacted the venue or placed an order.**

### What is now complete end to end

An approved directional view is carried from the option chain to a broker submission without
manual intervention. Proven against a recording broker:

```
submitted=True  state=EntrySubmitted  maxLoss=327.00  debit=3.27
clientOrderId=qd-opt-073acb89dd7b00e814a9c089-entry
```

The chain is discovered and priced at runtime, a defined-risk vertical is compiled, legs are mapped
to OCC symbols with open-side intents, and the reservation is committed to durable storage **before
any POST** — so an interrupted submission is recoverable by deterministic client order ID rather
than lost. The worst case is re-checked against the risk budget using the limit actually submitted,
not the compiled mid, so the entry buffer cannot widen the maximum loss past the cap.

Verified: **373 .NET tests**, 130 Python, ruff and mypy clean, Docker production image builds.

### Step 1 — credentials (only you can do this)

```powershell
$env:APCA_API_KEY_ID    = "<paper key>"
$env:APCA_API_SECRET_KEY = "<paper secret>"
```

The application refuses any host other than `paper-api.alpaca.markets`, and now refuses any
market-data host other than `data.alpaca.markets`.

### Step 2 — confirm the account can do what the lane needs

```powershell
docker compose up -d api
curl http://localhost:8080/api/system/capabilities
```

Required: `paperEnvironment: true`, `optionsTrading: true`, `optionsTradingLevel >= 2`. A spread is
gated at level 2; the lane refuses below it rather than sending an order that will bounce.

### Step 3 — export an option dataset

This is what unblocks volatility-risk-premium research, the highest-value remaining direction, and
it also proves the option clients against real venue responses for the first time.

```powershell
docker compose exec api curl -s localhost:8080/api/system/capabilities
```

The exporter is registered; a scheduled trigger is still outstanding (register item C3), so for now
invoke the research downloader directly:

```powershell
Set-Location research/python
uv run --frozen python -m quantdesk_research.data.alpaca_historical --symbols "XLK,XLF,XLE,XLV,XLI,XLY,XLP,XLU,XLB,XLRE,XLC" --timeframe 1Day --start 2018-11-01 --end 2026-08-28 --output data/US_EQUITIES_RESEARCH_001
```

Widening the universe this way is worth roughly 1.4x in detectability — real, but not decisive on
its own. See the research section for why.

### Step 4 — configure the run

For a defined-risk options trade on SPY:

```
QUANTDESK_AUTONOMOUS_MODE=ExperimentalPaper
QUANTDESK_AUTONOMOUS_ENABLED=true
QUANTDESK_AUTONOMOUS_SYMBOL=SPY
QUANTDESK_AUTONOMOUS_EXPRESSION=DefinedRiskVertical
QUANTDESK_AUTONOMOUS_ORDER_NOTIONAL=500
QUANTDESK_SYMBOLS=SPY,BTC/USD
```

`QUANTDESK_AUTONOMOUS_ORDER_NOTIONAL` is the hard cap on what one options opportunity can lose,
because a debit spread's maximum loss is exactly the premium paid. $500 admits the 600/605 SPY
vertical the tests exercise; $20 will reject every spread as `EntryLimitExceedsRiskBudget`.

`ExperimentalPaper` additionally requires the full authorization block — `QUANTDESK_EXPERIMENT_ID`,
`QUANTDESK_HYPOTHESIS_ID`, `QUANTDESK_STRATEGY_VERSION`, `QUANTDESK_EXPERIMENT_REGISTERED_AT`,
`QUANTDESK_EVIDENCE_REFERENCE`, and both sanity flags set true. It refuses to start otherwise. This
is the designed route for a bounded run that does not claim validated alpha, and it relaxes only
research-readiness gates — never risk, reservation, or reconciliation.

### Step 5 — watch it

```powershell
curl http://localhost:8080/api/autonomous/status
```

States to expect: `abstained` with a typed reason on most cycles, then `submitting_entry` and
`holding` when a spread is admitted. After that the multi-leg recovery worker owns the record —
fills, the durable hold, the managed exit, and final reconciliation all advance without the
evaluation cycle holding state.

### Be prepared for abstention, and read it as working

Most cycles will abstain, and that is the system behaving correctly rather than failing. The
likeliest reasons, all typed:

| Reason | Meaning |
| --- | --- |
| `EXPECTED_EDGE_BELOW_COSTS` | The momentum signal did not clear the venue hurdle. Most common. |
| `ExpectedValueBelowCosts` | A spread existed but the forecast could not pay for it. |
| `SpreadTooWide` | The chain was too illiquid at that moment. |
| `QuoteUnhealthy` | Option quotes were stale, crossed, or one-sided. |
| `EntryLimitExceedsRiskBudget` | Raise `ORDER_NOTIONAL` or accept narrower spreads. |
| `PortfolioUnreconciled` | Any pre-existing broker position or order halts entry. |

That last one matters operationally: entry halts on **any** open position or order, not only ones
for the traded symbol. Start from a flat account.

### The honest state of the research

No strategy qualifies. Four families cleared discovery; none beat passive equal-weight
out-of-sample. Microstructure and cross-asset lead-lag were researched on 2026-09-01 and both
rejected — one was a quintile-spread framing that is not a holdable position, the other a March
2020 crash artifact. Volatility risk premium is the strongest remaining direction and is blocked
only on the option dataset from step 3.

So `ExperimentalPaper` is the honest route to a first trade: a bounded, preregistered run that does
not claim validated alpha. `ValidatedPaper` will not fire, because nothing has qualified.

### What is still not done

* No option dataset has ever been exported, so **no option client has yet been run against the live
  venue.** They are no longer untested against its *shapes*, though: every option client is now
  exercised against Alpaca's documented payloads, and doing that found four defects that would have
  fired on the first real call (see "Option clients against real venue shapes" below). Treat a
  rejection as information, not a bug to suppress — but a rejection should now say what happened.
* The remaining option-lane risk is behavioural, not structural: strike coverage, spread width, and
  whether the venue's option feed is entitled at all. None of that can be known without credentials.

## Non-negotiable boundaries

- Alpaca PAPER only. Never add a live endpoint or fallback.
- C#/.NET is the sole order-mutation authority.
- Python, MCP, the Alpaca plugin/CLI, direct HTTP, and ad hoc scripts are research or diagnostics only.
- Do not remove or weaken R-gates, realistic costs, risk limits, reservation-before-POST, deterministic client IDs, lookup-before-retry, recovery, automatic exit, or reconciliation.
- `NO_TRADE`, `UNCERTAIN`, and `ABSTAIN` are valid outcomes.
- Do not treat the completed BTC diagnostic as evidence of alpha.
- Do not retune on an opened validation or holdout set.
- Do not enable autonomous execution until a fresh artifact genuinely qualifies and the actual runtime passes every preflight.

## Authoritative current state

- Repository: `C:\Users\Admin\OneDrive\Documents\New folder\QuantDesk`
- Branch: `build/csharp-foundation`
- Observed HEAD: `1800e89 Register multi-leg execution services and test Alpaca order mapping`
- The files previously listed as uncommitted are now committed in `1800e89`. Re-run `git status --short` before assuming any tree state.
- Running API, research API, and research worker containers were healthy at the last check.
- `GET /health` returned `Healthy`.
- `GET /api/autonomous/status` returned `state: disabled`, symbol `BTC/USD`, and no entry or exit order IDs.
- `GET /api/system/capabilities` returned PAPER true, equity true, crypto true, options true, options level 3, no reported problems, and unverified equity/option feed labels.
- Those containers were built before the latest MLeg lifecycle and option-history changes. Runtime health does **not** prove the new code is deployed.
- `CRYPTO-DIAGNOSTIC-2026-08-31-001` previously completed a bounded PAPER round trip and zero/zero reconciliation. It proved infrastructure only.

## Current uncommitted files

None. All work described in this handoff is committed on
`codex/asset-class-routing-and-debt-audit`, branched from `build/csharp-foundation`. Run
`git status --short` for the live state rather than trusting this line.

## Work completed and verified in this continuation

### Research qualification plumbing

- Typed per-gate evidence, rule publication, independent-validation worker orchestration, and rejected-hypothesis memory were exercised together.
- Ruff passed.
- Strict mypy passed.
- Focused Python tests passed: **23 passed**.
- This proves plumbing behavior only; it does not qualify a strategy.

### MLeg request adapter and OCC handling

- Exact Alpaca PAPER `order_class: mleg` payload construction is present.
- Invalid MLeg commands return rejection without a broker POST.
- Invalid success responses are classified as ambiguous.
- Strict OCC parsing and deterministic dynamic option slots are present.
- Unknown broker instruments remain visible and cause reconciliation failure instead of being dropped.
- Domain tests passed: **16 passed**.
- Runtime tests passed at that point: **84 passed**.
- Alpaca tests passed at that point: **34 passed**.
- API tests passed at that point: **57 passed**.

### Durable MLeg lifecycle foundation

Implemented in the working tree:

- `MultiLegExecutionStore` with atomic file replacement.
- Durable entry and exit commands, deterministic IDs, maximum holding period, defined maximum loss, broker IDs, fill quantities/prices/timestamps, reconciliation timestamps, and failure reason.
- Atomic entry-submission claim and exit-reservation/submission claim.
- `MultiLegExecutionLifecycle` with PAPER checks, deterministic entry/exit IDs, reservation-before-POST, lookup before submission, accepted/new/pending/partial/filled/rejected/canceled/expired tracking, durable hold/exit time, ambiguous recovery, exit submission, restart enumeration, and zero-exposure completion checks.
- Ambiguous HTTP outcomes and ambiguous broker results query by the original deterministic client ID before stopping. They do not create a new ID.
- `MultiLegExecutionRecoveryService` is registered as a hosted worker in `Program.cs`.

Verified lifecycle tests:

- reservation is durable before broker submission;
- repeated advancement produces at most one entry POST;
- timeout after broker acceptance recovers the same order across restart;
- ambiguous result recovers by client ID;
- accepted -> partial -> filled persists for entry and exit;
- durable holding and exit progression works;
- zero broker/internal exposure reaches Complete;
- deterministic commands and holding policy survive store reconstruction;
- duplicate client IDs are rejected.

Focused lifecycle result after the final ambiguity fix: **5 passed**.
Full Runtime result before that final focused fix: **88 passed**. Re-run the full Runtime suite.

### Read-only option research acquisition foundation

- `AlpacaHistoricalOptionBarClient` implements the documented paginated `/v1beta1/options/bars` path for one to 100 strict OCC symbols, chronological deduplication, conflict detection, provenance-preserving timestamps, and rejection of unrequested symbols.
- Its tests passed as part of Alpaca **36 passed**.
- API tests passed again: **57 passed**.
- `AlpacaOptionContractClient` was then added for paginated active/inactive SPY contract discovery with strict OCC, underlying, strike, expiration, status, and tradability validation.
- The test command for the option-contract client was **interrupted** before any result. Treat this newest client as unverified.

### New cross-asset mechanism discovery

Added a fixed, causal relative-strength/reversal discovery family across SPY, QQQ, IWM, and DIA:

- immutable SIP/all daily hashes;
- signal known at the prior close;
- entry at the next session open;
- non-overlapping holds;
- BASE/STRESS costs unchanged at 25/35 bps;
- prior 20 comparisons plus six new comparisons charged;
- separate discovery, validation, and holdout slices.

Verification:

- Ruff passed.
- Strict mypy passed.
- Unit tests: **3 passed**.

Discovery result: all six candidates failed. The best candidates had positive means but negative confidence bounds and Sharpe below 0.5. Validation and holdout were not opened. Do not promote, tune on later phases, or describe this family as qualified.

## Verification completed 2026-08-31

Every item of the previous immediate verification debt was executed with .NET SDK 10 in a container and
with `uv` for Python. Results:

- `dotnet restore` + full solution build: succeeded, **0 warnings, 0 errors** (`TreatWarningsAsErrors`
  is on, so this is a clean analyzer pass).
- Full solution tests, all five projects in one run: Domain **16**, Alpaca **56**, Architecture **2**,
  Runtime **89**, API **63** — **226 passed, 0 failed, 0 skipped**. Repeated three times consecutively.
- The previously interrupted Alpaca command now completes; `AlpacaOptionContractClient` is no longer
  unverified.
- Python: Ruff **all checks passed**; strict mypy **no issues in 95 source files**; full pytest
  **91 passed**.
- Docker production build (`Dockerfile` -> `quantdesk-api:verify`): succeeded on the current tree.
- `git diff --check` clean; no secrets, credentials, or generated artifacts in the diff; new files are
  LF as `.gitattributes` requires.

These are build and test facts only. They prove nothing about alpha, and no strategy qualifies.

## Option data acquisition work completed 2026-08-31

### `AlpacaOptionContractClient` hardened and verified

The client was reviewed against the official Alpaca `GET /v2/options/contracts` schema. It previously
read only `id`, `symbol`, `strike_price`, `status`, and `tradable`, and cross-checked nothing, so a
broker payload that disagreed with its own OCC symbol would have entered a dataset silently. It now
rejects, with a symbol-specific message, any contract whose:

- `underlying_symbol` is not the requested underlying;
- `root_symbol` differs from the underlying or from the OCC root (an adjusted/non-standard contract);
- `expiration_date` contradicts the OCC symbol, or falls outside the requested expiration window;
- `type` contradicts the OCC-encoded right;
- `strike_price` contradicts the OCC-encoded strike;
- `status` does not match the requested status;
- `multiplier` or `size` is not the standard 100 — a non-standard deliverable would miscompute defined
  maximum loss and per-contract economics;
- `style` is not `american`/`european`;
- `id` is missing.

Duplicate symbols with differing definitions now raise a conflict instead of overwriting. `multiplier`,
`size`, `root_symbol`, and `style` are captured on `AlpacaOptionContract` because admission economics
need them.

### Pagination is bounded on every Alpaca acquisition path

`AlpacaPageCursor` guards the `page_token` loop: a repeated token or a page budget overrun fails closed.
It is applied to the option-contract, option-bar, and stock-bar clients, all three of which previously
looped unbounded on a broker that echoed a token.

### Option bars validate provenance

`AlpacaHistoricalOptionBarClient` already rejected unrequested symbols and conflicting duplicates. It
now also rejects a bar outside the requested window, a non-positive price, negative volume/trade count,
and inconsistent OHLC.

### Immutable, hashed option dataset export

`OptionResearchDatasetExporter` (registered in `Program.cs`) publishes two artifact kinds atomically via
temp-file-and-replace, each with an `OptionDatasetManifest` carrying dataset id, kind, underlying,
status, expiration window, timeframe, request window, row count, page count, **every paginated source
URI**, SHA-256, generation time, and data file:

- `option-contract-snapshot` — the discovered contract universe. Refuses to publish an empty universe.
- `option-bar-dataset` — bars for exactly the snapshot's contracts, bound to the snapshot's SHA-256 and
  to the underlying equity dataset SHA-256 so the three cannot drift apart. Refuses to publish when any
  requested contract returned no bars, so gaps cannot pass silently.

Source URIs carry no credentials; Alpaca keys travel in headers. A test asserts this.

Both clients now return `OptionContractQuery` / `OptionBarQuery`, which carry the request URIs alongside
the data, so provenance is a return value rather than something reconstructed later.

New tests: 18 in Alpaca (38 -> 56), 6 in API (57 -> 63).

### What this does not do

It does not select contracts, price spreads, or authorize anything. Contract discovery and bar
acquisition are read-only research inputs. No execution path consumes them yet.

## Why nothing was qualifying — diagnosis of 2026-08-31

Three compounding defects, not a shortage of good hypotheses, were preventing qualification. All
three are now fixed and the fixes are verified. A fourth constraint is real and remains.

### Defect 1: equity costs were overstated roughly eightfold

`BASE_COST` charged 25 bps round trip to SPY, QQQ, IWM, and DIA. Alpaca is **commission-free**
for US equities; regulatory fees apply to sells only and are sub-basis-point; and these four ETFs
quote a one-cent spread almost continuously, which is 0.15-0.4 bps at their price levels. An
honest round trip is near 2 bps. The ladder is now 5/10/20 bps (BASE/STRESS/SEVERE), still
roughly double the achievable cost.

Effect, measured on the already-opened relative-strength discovery phase: the best candidate went
from mean +24.04 bps and Sharpe 0.300 to mean **+55.13 bps and Sharpe 0.680**. Two candidates
crossed the Sharpe gate purely from removing the modelling error. Overstating cost is not
conservatism; it rejects real edges exactly as understating it accepts false ones.

### Defect 2: per-trade measurement made the evidence untestable

The experiments held one ETF at a time and tested the mean *per-trade* return. That carries full
single-asset variance (about 230 bps per 10-session hold) and yields roughly 80 non-overlapping
trades per phase. Detecting a realistic edge needed 366 trades; 83 existed.

Evaluation now runs at portfolio level on daily returns
(`quantdesk_research/backtest/portfolio.py`), giving ~1,900 observations over the same history.
Costs are charged on realised turnover every session, and standard errors are Newey-West so the
serial correlation of slow signals cannot inflate significance.

### Defect 3: the confidence gate was arithmetically unsatisfiable

The gate required a Bonferroni-corrected one-sided lower bound above zero *and* a minimum
annualised Sharpe of 0.5. Those clauses contradict each other:

| Requirement | Years of daily data needed |
| --- | --- |
| Sharpe 0.5, Bonferroni over 26 prior comparisons | 33.4 |
| Sharpe 0.6, Bonferroni over 26 | 23.2 |
| Sharpe 0.5, Bonferroni over 40 | 36.6 |

Only 7.8 years exist, of which discovery holds 3.9. On 3.9 years the bound admits only strategies
showing Sharpe above **1.46**; on the full history, above 1.03. A four-ETF strategy showing
Sharpe 1.46 over 3.9 years is far more likely an overfitting artefact than a real edge, so the
gate did not merely reject good strategies — it **selected for** overfitted ones, inverting its
own purpose. That is an artificial blocker, not a valid research rejection.

The gate is **restructured, not relaxed**. Every substantive requirement is kept — positive net
expectancy, Sharpe above 0.5, a positive lower confidence bound, positive stress-cost expectancy
and holdout sub-window stability — and two are added: every family must **beat the passive
equal-weight benchmark**, and it must **replicate out-of-sample**. Multiplicity is now controlled
by sequential discovery -> validation -> holdout replication, which tests the property actually
wanted. The removed piece is only the in-sample Bonferroni correction. The arithmetic is recorded
in `gate_reasons` so the decision is auditable.

### The constraint that remains: universe breadth

With the three defects removed, 14 mechanism-based families were evaluated
(`quantdesk_research/experiments/equity_portfolio_strategies.py`). Four cleared discovery, led by
`vol-scaled-trend-126d` at Sharpe 0.604 and 12.0% annualised net. **None beat passive
equal-weight on validation.** Every cross-sectional family is negative in every phase.

The reason is measurable: SPY, QQQ, IWM, and DIA have a **mean pairwise daily-return correlation
of 0.859**. They are one asset with noise. Cross-sectional dispersion is 47 bps/day against 137
bps/day of single-asset volatility, so a market-neutral spread has roughly a third of the signal
amplitude and none of it survives cost.

**This is now the binding constraint, and it is fixable.** The universe is a parameter
(`--symbols`), and the immutable downloader already accepts a symbol list. Widening to sector and
factor ETFs (XLK, XLF, XLE, XLV, XLI, XLY, XLP, XLU, XLB, XLRE, XLC, plus size and style funds)
decorrelates the cross-section, which is exactly what the cross-sectional mechanisms need. That
requires Alpaca market-data credentials, which were not available in this session:

```powershell
$env:APCA_API_KEY_ID="..."; $env:APCA_API_SECRET_KEY="..."
Set-Location research/python
uv run --frozen python -m quantdesk_research.data.alpaca_historical --symbols "XLK,XLF,XLE,XLV,XLI,XLY,XLP,XLU,XLB,XLRE,XLC" --timeframe 1Day --start 2018-11-01 --end 2026-08-28 --output data/US_EQUITIES_RESEARCH_001
uv run --frozen python -m quantdesk_research.experiments.equity_portfolio_strategies --data-root data/US_EQUITIES_RESEARCH_001 --phase discovery --summary --symbols "SPY,QQQ,IWM,DIA,XLK,XLF,XLE,XLV,XLI,XLY,XLP,XLU,XLB,XLRE,XLC"
```

### Crypto: a structural finding, not a research failure

Alpaca charges 0.25% taker and 0.15% maker per side for spot crypto at tier 1, so a taker round
trip costs **50 bps in fees before any spread**. Every BTC and ETH campaign in the failure ledger
landed near minus the cost allowance because the venue fee, not the signal, dominated. Short-
horizon crypto on this venue is structurally unprofitable. Do not spend further research budget
on it without either a much larger per-trade edge or a holding period long enough to amortise
50 bps.

### Honest status

No strategy qualifies, and `NO_TRADE` remains correct — but it is now a *trustworthy* NO_TRADE.
The previous rejections were uninterpretable because they mixed real economic failure with an
eightfold cost error, an underpowered estimator, and an unsatisfiable gate. Those are gone. The
machinery now demonstrably detects signal when signal is present; it reports that this particular
four-ETF universe does not contain tradeable alpha.

## End-to-end trade lifecycle trace, 2026-08-31

`tests/QuantDesk.Api.Tests/AutonomousLifecycleTraceTests.cs` now drives one opportunity through
every stage the application owns and records each stage's verdict. It uses a recording broker, so
it places no order and needs no credentials. Run it whenever "why did nothing trade" needs an
answer.

### The pipeline works end to end

With a strong enough signal the full path completes:

```
market-evidence      bid=100 ask=100.01 bars=13
committee            actionable=True expectedBps=248.54 experts=2
strategy-compiler    candidate=crypto-long-momentum-v1 exit=crypto-long-managed-v1
cost-model           total=0.1220 usd
risk-governor        approved=True reason=Approved
reservation          committed id=1 (before any broker call)
broker-submission    state=Acknowledged clientOrderId=qd-trace-entry-0001 qty=0.19998000
outcome              order reached the broker adapter
```

Reservation-before-POST, deterministic ordering, and the risk veto all behave correctly. **There
is no plumbing defect between research and the broker adapter.**

### The actual blocker: venue cost, not signal logic

The first gate is `CryptoResearchGate`, which admits an opportunity only when the weaker of two
momentum horizons exceeds `spread + fees + slippage + minimum edge`. Sweeping the 13-bar move
through the identical pipeline, changing only the venue cost profile:

| 13-bar move | spot crypto (50 bps fees) | US equity (commission-free) |
| --- | --- | --- |
| 0.10% | blocked | blocked |
| 0.25% | blocked | blocked |
| 0.50% | blocked | **submitted** |
| 0.75% | blocked | **submitted** |
| 1.00% | blocked | **submitted** |
| 2.00% | blocked | **submitted** |
| 4.00% | submitted | submitted |

Spot crypto has to predict a move between 2% and 4% within roughly an hour before the application
will place an order. That effectively never happens, and when it does it is a volatility spike —
precisely where short-horizon momentum is least reliable. The same signal on a commission-free
venue is admissible from 0.5%, an order of magnitude earlier.

**This is why no trade has ever been placed.** The only implemented autonomous lane is spot
crypto, which is the one asset class whose fee structure makes short-horizon trading
inadmissible. It is not a bug in the gate; the gate is doing its job with honest numbers.

### Fixed in this pass

1. **Hardcoded cost constants removed.** `RoundTripTakerFeeBps`, `RoundTripSlippageAllowanceBps`,
   and `MinimumNetEdgeBps` were `private const` inside the crypto gate. They are now an injected
   `ExecutionCostProfile` with `SpotCryptoTaker`, `SpotCryptoMaker`, and `UsEquity` presets, each
   citing the Alpaca schedule it came from. The crypto default is unchanged, so behaviour is
   identical for existing callers. Decisions now also record the asset class and the exact hurdle
   they had to clear.
2. **Hardcoded account capabilities removed.** The pipeline passed a literal
   `new AccountCapabilities(true, false, true, false, null)` to the compiler on every cycle. That
   asserted the endpoint was PAPER without checking, and declared **equity and options trading
   unavailable** regardless of the real account — which reports equity true and options level 3.
   No equity or option candidate could ever be compiled. Capabilities are now a parameter.

### Blockers still in the path, in priority order

1. **No options execution path.** `AutonomousDecisionPipeline` compiles through
   `CryptoDirectionalStrategyCompiler` only. Passing honest capabilities stops the lie but does
   not create an options candidate. The hackathon requires options in every strategy, and the
   MLeg lifecycle built earlier is not wired into the decision pipeline. This is the largest
   remaining gap.
2. **No equity execution path.** The lane sources evidence from
   `AlpacaLatestCryptoQuoteClient`, so equities cannot flow end to end even though the gate now
   accepts them. An equity evidence source and compiler are required.
3. **Market orders are hardcoded**, so crypto always pays the 25 bps taker rate. Resting a limit
   order would pay 15 bps and cut the round trip from 50 to 30 bps.
4. **Generated client order IDs.** `$"qd-auto-entry-{Guid.NewGuid():N}"` is not restart
   recoverable, unlike the durable diagnostic lane.
5. **Entry halts on any broker position or order**, not merely ones for the traded symbol, so a
   single unrelated leftover position blocks the lane permanently.

## Blocker removal, 2026-08-31 (second pass)

Worked the blocker list in priority order. Note one dependency that reorders it in practice: an
options vertical needs a directional view on an underlying, so the equity path is a prerequisite
for the options path rather than a competitor to it. The shared routing envelope below was built
first because all three lanes need it.

### Shared opportunity routing envelope — new

`OpportunityRoute` / `OpportunityRouter` classify a symbol into exactly one supported asset class
and carry everything admission needs: the venue cost profile, the order-pricing policy, and the
account permission that class requires. Previously those three were hardcoded to spot crypto in
four separate places inside the decision pipeline, so adding an asset class meant editing the
pipeline. An unrecognised symbol now fails closed in one place instead of silently taking the
crypto path — a wrong route would apply the wrong cost model and the wrong permission check to
real money movement.

Hurdle per class on a one-basis-point spread, which is the number that decides admissibility:

| Route | Hurdle | Why |
| --- | --- | --- |
| spot crypto (taker) | 71 bps | 0.25%/side venue fee dominates |
| US equity | 9 bps | commission-free; sub-bp regulatory |
| US equity option | 32 bps | no commission, but two spreads each way |

### 3. Hardcoded market orders — fixed

`OrderExecutionPolicy` replaces the hardcoded `ExecutionOrderType.Market`. A market order is the
most expensive and least safe choice available: it guarantees the taker rate and accepts whatever
price the venue returns, with no cap on how far through the quote the fill lands. Every route now
prices a **marketable limit** — crossing the touch by a bounded 10 bps so it still fills, while
refusing a price worse than that. This is a loss-prevention change, not a cost optimisation:
it bounds what a thin or fast-moving book can take.

### 2. No equity execution path — fixed

`AlpacaLatestEquityQuoteClient` supplies the live NBBO and recent closes in exactly the shape the
decision pipeline already consumes. Its absence is what made the lane crypto-only: the evidence
parameter was satisfiable solely by the crypto client, so an equity opportunity could not reach
the committee, compiler, or risk regardless of what research said. A short bar series is returned
intact rather than padded, so the gate rejects it instead of trading fabricated momentum.

### 1. No options execution path — compiler built

`DefinedRiskVerticalCompiler` turns a directional view into a two-leg debit vertical whose worst
case is known and capped before the order is sent.

Why a debit vertical rather than a single long option or anything short-premium: **the maximum
loss of a debit spread is exactly the net premium paid.** It cannot gap through a stop, cannot be
assigned into an unbounded liability, and needs no margin beyond the debit. The compiler refuses
any spread whose debit exceeds the caller's risk budget, so the most one options opportunity can
lose is a number chosen in advance.

It prices conservatively — pays the offer on the long leg, receives the bid on the short — because
assuming mid fills would understate the debit, and the debit *is* the maximum loss. Rejections are
typed: `DebitExceedsSpreadWidth` (cannot profit), `QuoteUnhealthy`, `SpreadTooWide`,
`RewardToRiskTooLow`, `ExpectedValueBelowCosts`, and others each mean something different and are
reported distinctly.

### 4. Random client order IDs — fixed

`DeterministicClientOrderId` derives the ID from the opportunity's identity instead of
`Guid.NewGuid()`. When a POST's outcome is ambiguous, the only safe recovery is to ask the broker
whether the order already exists, by client-order ID — and that lookup is possible only if the ID
can be recomputed. A random ID is unrecoverable by construction: once the process forgets it, the
order can neither be found nor ruled out, leaving only "halt" or "risk a duplicate". The scheme
had been copy-pasted into the diagnostic and multi-leg lanes; this is now the single definition.

**Not yet fully solved.** The ID is deterministic given the opportunity, which fixes recovery
within a cycle. Recovery *across a restart* additionally needs the opportunity persisted before
the POST, the way `MultiLegExecutionStore` already does for the options lane. The autonomous lane
should adopt that store.

### 5. Entry halts on any broker position — deliberately not changed

Scoping this check to the traded symbol would weaken the no-unexplained-exposure invariant, which
`AGENTS.md` forbids. Doing it safely needs position attribution — the ability to say a given
position belongs to a known strategy — which does not exist yet. Left as is, with the reasoning
recorded, rather than traded away for convenience.

### Verification

Full solution: **279 tests passing**, 0 failures, 0 warnings, all five assemblies.

### Remaining work to reach an actual options trade

1. Wire `DefinedRiskVerticalCompiler` into `AutonomousDecisionPipeline`, selected by route.
2. Add a live option quote/chain source. `AlpacaOptionContractClient` discovers contracts but
   there is no live option NBBO client yet, and a vertical cannot be priced without one.
3. Select the evidence source by route in the pipeline instead of taking the crypto client.
4. Register the new services in `Program.cs`.
5. Give the autonomous lane the durable store so item 4 above is fully closed.

## Options and equity lanes wired, 2026-08-31 (third pass)

The four items that stood between the compiler work and an actual options trade are done. The
options lane now runs end to end in code, from a directional view to a priced, risk-defined spread.

### Live option pricing — the hard blocker, now removed

`AlpacaLatestOptionQuoteClient` reads `/v1beta1/options/quotes/latest`. Contracts could previously
be discovered but not priced, and a defined-risk vertical cannot be compiled without a bid and an
offer on both legs — the net debit *is* the maximum loss, so it has to come from real quotes rather
than an estimate.

A quote that is missing, crossed, one-sided, or stale is marked `Stale` rather than dropped, so the
compiler sees an unusable leg and refuses the spread instead of pricing off whatever remains. Every
requested slot is always represented in the result, so absence can never be mistaken for health.

### The join that was missing

`OptionVerticalOpportunityService` connects discovery to pricing to compilation — the three pieces
existed but nothing linked them, so no options candidate could ever be produced. It selects a
bounded strike band around spot (Alpaca prices at most 100 symbols per request, and quoting a whole
chain to pick two strikes is wasteful), registers deterministic instrument slots, prices them, and
compiles.

Observed end to end against stubbed venue responses, SPY at 600 with a +200 bps view:

```
considered=2 priced=2 reason=Admitted maxLoss=320.0 maxProfit=180.0
```

A 600/605 call vertical: $320 debit, $180 maximum profit, $500 width. **The $320 is the entire
downside** and it is refused outright if it exceeds the configured risk budget.

### Evidence now follows the route

`MarketEvidenceProvider` selects the evidence source from the route. The autonomous service called
the crypto quote client directly, which is what kept the lane crypto-only in practice even after
the gates were generalised — an equity symbol had no way to obtain evidence. An asset class with no
configured source now fails loudly rather than falling back to the crypto venue. An option routes
to its *underlying's* evidence, because the directional view is formed on the underlying and the
spread is only how it is expressed.

### The autonomous service is now route-driven

`AutonomousPaperTradingService` routes the configured symbol before anything else, probes the live
account, and refuses an asset class the account does not permit. The hardcoded
`AccountCapabilities` literal is gone from the path entirely — capabilities come from
`IAlpacaCapabilityProbe`.

### Registered

`AlpacaLatestOptionQuoteClient`, `AlpacaLatestEquityQuoteClient`, `OpportunityRouter`,
`MarketEvidenceProvider`, `DefinedRiskVerticalCompiler`, and `OptionVerticalOpportunityService` are
all wired in `Program.cs`. The vertical's risk budget derives from the same notional envelope the
spot lane uses rather than being invented separately.

### Verification

Full solution: **292 tests passing**, 0 failures, 0 warnings, all five assemblies.

### What is still not done

1. The autonomous service compiles a *spot* candidate and submits it. Routing an option candidate
   into `MultiLegExecutionLifecycle` — which already exists and is tested — is the last wiring step
   before an options order can be submitted.
2. The autonomous lane still lacks the durable store, so restart recovery is not yet equivalent to
   the diagnostic lane even with deterministic IDs.
3. No credentials have been present in any session, so nothing above has touched the live venue.
   Every result is against stubbed responses.

## Architecture and technical-debt audit — 2026-08-31

A systematic pass over every domain. Findings are graded by what they cost: **Critical** means it
can lose money or hide a loss; **High** means it blocks or misleads work; **Medium** is real debt
with a workaround; **Low** is hygiene. File and line references are from the current tree.

### What is sound — do not "fix" these

Recording this so a later pass does not churn working code:

* **Layering is clean.** `QuantDesk.Domain` has zero project references and no `System.IO`,
  `HttpClient`, `JsonSerializer`, or `ILogger` usage. `Runtime` and `Alpaca` depend only on
  `Domain`. Dependencies point inward, as they should.
* **No TODO, FIXME, HACK, or `NotImplementedException` anywhere** in `src/` or `harness/`.
* **No empty or swallowing catch blocks.**
* **Atomic persistence is genuinely atomic** — temp file then `File.Move(..., overwrite: true)` —
  wherever it appears.
* Ambiguous-submit recovery, reservation-before-POST, and the PAPER host check on the *trading*
  endpoint are all real and tested.

---

### QuantDesk.Domain

Cleanest project in the repository. No findings above Low.

* **Low — `OptionQuoteSnapshot` carries five always-null greeks.** `ImpliedVolatility`, `Delta`,
  `Gamma`, `Vega`, `Theta` are populated by nothing; every producer passes null. Either compute
  them (`BlackScholes` already exists and is nearly untested) or drop them from the record. As it
  stands the type advertises data the system never has.

---

### QuantDesk.Runtime

* **High — safety-critical classes are each covered by exactly one test file.** `BlackScholes`,
  `OptionChainValidator`, `ExecutionJournalReplay`, and `PythonResearchContractReader` decide
  whether option pricing and research evidence are trustworthy. One test file each is thin.
* **Medium — atomic file-write is copy-pasted six times.** `DiagnosticExecutionStore:239`,
  `MultiLegExecutionStore:184`, `PortfolioSnapshotStore:16`, plus three copies in `Api`
  (`HistoricalCryptoDatasetService:188`, `HistoricalEquityDatasetService:75`,
  `OptionResearchDatasetExporter:180`). Same knowledge, six authorities. One `AtomicFileWriter`
  in `Runtime.Persistence` should own it. Clearest DRY violation in the codebase.
* **Medium — 16 separate `new JsonSerializerOptions(JsonSerializerDefaults.Web)` instantiations.**
  Each is a chance for one reader and one writer to disagree about casing or number handling.
  Should be one shared, immutable, cached instance per contract boundary.

---

### QuantDesk.Alpaca

* **Critical — the PAPER-only invariant is not enforced on market-data endpoints.**
  `AlpacaOptions.FromEnvironment` rigorously validates that the *trading* host is
  `paper-api.alpaca.markets`, but `https://data.alpaca.markets` is hardcoded as a string literal
  in **eight** places: `AlpacaHistoricalOptionBarClient:46`, `AlpacaHistoricalStockBarClient:25`,
  `AlpacaLatestCryptoQuoteClient:49,76,97`, `AlpacaLatestEquityQuoteClient:45,70`,
  `AlpacaLatestOptionQuoteClient:59`. Two consequences: the host cannot be pointed at a mock or a
  replay for testing, and there is no single place where a data-endpoint change is reviewed. Graded
  Critical because the repository's central safety claim is "only the paper host is ever
  contacted", and that claim is currently enforced on one of two hosts.
* **High — `CryptoMarketEvidence` is now the evidence type for equities and options.** Introduced
  when adding `AlpacaLatestEquityQuoteClient` and `MarketEvidenceProvider` — my own debt, recorded
  here rather than left for someone else to find. It is a ubiquitous-language violation: the type
  name asserts an asset class it no longer describes, and a reader will reasonably assume equity
  evidence flows through a crypto path. Rename to `DirectionalMarketEvidence` and move it out of
  the crypto client's file.
* **Medium — `AlpacaTradingGateway` is 404 lines** and owns account reads, asset reads, submit,
  lookup, open orders, positions, cancel, replace, close-position, and multi-leg mapping. Not yet a
  god class, but it is the second-largest file and grows with every asset class.
* **Medium — `AlpacaHistoricalStockBarClient` silently ignores unrequested symbols and a null
  payload.** The option clients were hardened to reject both; the stock client received only the
  pagination-cursor fix. It should fail closed the same way.

---

### QuantDesk.Api

* **Critical — `AutonomousPaperTradingService` has zero tests.** Verified by grep: no test file
  references it. This is the class that decides whether to trade, halts on unreconciled state,
  reserves capital, submits the order, manages the position, and exits. Every other money-path
  component is tested. The single most important untested class in the repository.
* **Critical — risk limits are unnamed magic numbers in the composition root.** `Program.cs:107`:
  `new RiskLimits(new Usd(5), new Usd(25), new Usd(100), new Usd(250), 1, 100_000, 100_000,
  100_000, 0.01, 1)`. No configuration, no environment override, no provenance comment. Worse,
  **the three greeks limits are inert**: `MaximumAbsDollarDelta`, `MaximumAbsDollarGamma1Pct`, and
  `MaximumAbsDollarVega1Vol` are all 100,000 against a $20 notional envelope, so they can never
  bind. The system appears to have greeks risk limits and does not — which matters now that an
  options lane exists, because greeks are exactly how option risk is measured.
* **Critical — `CryptoDiagnosticExecutionService` is a 1,072-line god class with 37 methods.**
  Three times the next largest file. It owns admission, persistence, submission, recovery, fill
  tracking, holding, exit, emergency flatten, and reconciliation. Every one of those is a separate
  reason to change. The durable lifecycle logic inside it is good, and is exactly what the
  autonomous lane still lacks — it should be extracted into a reusable lifecycle rather than
  reimplemented a third time.
* **High — `HistoricalEquityDatasetService` is hardcoded to SPY** (`:51`, `:65`). A direct cause of
  the research universe being stuck at four correlated ETFs: the application can only ever publish
  one equity dataset, so QQQ, IWM, and DIA had to be side-loaded manually. Widening the research
  universe — the highest-value research action available — requires changing this service, not
  passing a flag.
* **High — `ExecutionAdmissionPolicy` and `CryptoFeeSchedule` have zero tests.** One is an
  admission gate; the other is the provenance of the cost numbers that decide admissibility.
* **Medium — `Program.cs` is a 388-line composition root** mixing service registration with inline
  endpoint definitions and inline lambdas carrying business defaults.
* **Medium — the dataset manifest is declared three times** across two languages:
  `HistoricalCryptoDatasetService:8`, `OptionResearchDatasetExporter:9` (as `OptionDatasetManifest`),
  and `research/python/.../alpaca_historical.py:27`. The C# writer and the Python reader can drift
  with nothing catching it.
* **Medium — the autonomous symbol defaults to `BTC/USD`** (`AutonomousPaperTradingOptions:22`),
  the one venue proven structurally unprofitable at short horizons. The default points at the lane
  that cannot trade.
* **Low — `Program.cs:311` passes a magic `0.00000001m`** into a diagnostic endpoint unexplained.

---

### Research plane (Python)

* **Critical — 1,104 lines across 19 modules are dead code**, never imported by any module or test.
  Verified by import-path search, not filename matching:

  `clocks.py`, `contracts/episode.py`, `contracts/policy_proposal.py`, `data/duckdb_catalog.py`,
  `data/parquet_io.py`, `evaluation/actionability.py`, `evaluation/purged_cv.py`,
  `evaluation/walk_forward.py`, `evaluation/deflated_sharpe.py`, `evaluation/pbo.py`,
  `experiments/equity_campaign.py`, `experiments/promotion_evidence.py`,
  `experiments/sqlite_registry.py`, `features/cross_asset.py`, `models/artifact_export.py`,
  `models/classifier.py`, `models/garch.py`, `models/hmm.py`, `options/surface_checks.py`.

  Graded Critical for a specific reason. **The dead set includes the entire overfitting-control
  toolkit** — deflated Sharpe ratio, probability of backtest overfitting, purged cross-validation,
  and walk-forward analysis. Those are precisely the tools that would have prevented the
  unsatisfiable-Bonferroni-gate problem documented above, and they were written and then never
  connected to a single experiment. The repository looks considerably more statistically rigorous
  than it behaves.

  `experiments/equity_campaign.py` (332 lines) is also dead, despite being the campaign this
  handoff refers to as `US_EQUITIES_RESEARCH_001`.
* **Medium — `crypto_direction.py` is 810 lines**, the largest module by more than double, and it
  serves a lane the cost analysis has already ruled out.

---

### Cross-cutting

* **High — fourteen environment variables are read by code but absent from `.env.example`.**
  Most serious: **`QUANTDESK_AUTONOMOUS_MODE`**, which selects `Disabled` / `ExperimentalPaper` /
  `ValidatedPaper` and is the single most safety-critical setting in the system. Also missing:
  `QUANTDESK_DIAGNOSTIC_STORE_PATH`, `QUANTDESK_MLEG_STORE_PATH`, `QUANTDESK_RESEARCH_ARTIFACT_ROOT`,
  `QUANTDESK_RESEARCH_BASE_URL`, `QUANTDESK_RESEARCH_DATA_ROOT`, `QUANTDESK_EQUITY_LOOKBACK_DAYS`,
  and the six `QUANTDESK_EXPERIMENT_*` / `QUANTDESK_*_SANITY_PASSED` variables that constitute the
  entire ExperimentalPaper authorization surface. An operator reading `.env.example` cannot
  configure, or even discover, the authorization path.
* **Medium — 74 files in `Docs/` against roughly 11,000 lines of C#.** Documentation volume exceeds
  what the code can keep true, and at least one file
  (`Docs/AUTONOMOUS_TRADING_CONNECTION_AUDIT.md`) describes the autonomous path as it was before
  the routing work. Docs drift is not caught by any test.
* **Medium — test-name-to-source mapping is unreliable.** Several classes are covered under
  differently-named test files, which made this audit's first coverage pass produce false
  positives. A naming convention would make coverage gaps mechanically checkable.

---

### Suggested order of work

Ordered by risk removed per unit of effort, not by domain:

1. Test `AutonomousPaperTradingService` — the untested money path.
2. Make risk limits configurable, named, and provenance-documented; set greeks limits that can
   actually bind, or remove them and stop implying they exist.
3. Centralise the Alpaca data host and bring it under the same PAPER-only validation as trading.
4. Either wire the overfitting toolkit into the experiments or delete it; leaving it dead
   misrepresents the system's rigour.
5. Extract the durable lifecycle from the diagnostic god class so the autonomous lane can reuse it
   instead of a third reimplementation.
6. Rename `CryptoMarketEvidence`; un-hardcode SPY in the equity dataset publisher.
7. Document the fourteen missing environment variables.

## Audit findings fixed in the same pass — 2026-08-31

Seven findings from the audit above were repaired immediately because each was contained and
carried real risk. Test count went from 292 to **335**, all passing.

### Critical — the PAPER-only invariant now covers market data

`AlpacaOptions` gained a validated `DataBaseUrl` and a `DataUri(...)` composer. All eight hardcoded
`https://data.alpaca.markets` literals are gone; every market-data client composes against the one
validated host. The data host is now validated exactly as the trading host is — HTTPS, one approved
hostname, rejected otherwise — and is overridable via `APCA_API_DATA_URL` so the clients can be
pointed at a replay. `AlpacaOptionsTests` pins both halves, including rejection of
`https://api.alpaca.markets`, `http://` downgrades, and unapproved hosts.

### Critical — risk limits are configurable, scaled, and can actually bind

`RiskLimitOptions.FromEnvironment(orderNotional)` replaces the ten unnamed literals in the
composition root. Every limit derives from the order notional so the envelope scales with position
size, and each is individually overridable; an invalid or non-positive override falls back to the
safe default rather than disabling the limit.

The three greeks caps were the important part. They were 100,000 against a $20 envelope and could
never bind. They are now 3x notional for dollar delta and 1x for gamma and vega — limits that bind
before an unintended naked position could pass. This mattered little when the only lane was spot
crypto, which has no gamma or vega; it matters now that a defined-risk options lane exists, because
greeks are precisely how option risk is measured. `RiskLimitOptionsTests` pins the scaling, the
ordering from tightest to loosest, and the fallback behaviour.

### Medium — one authority for atomic persistence

`AtomicFile` in `Runtime.Persistence` replaces six near-identical private helpers. It also fixes a
latent flaw all six shared: none cleaned up the temporary file when a write failed, and
`PortfolioSnapshotStore` used a fixed `.tmp` name that two writers could corrupt. `AtomicFileTests`
covers overwrite-shrinking a file, a mid-write serializer failure leaving the previous complete
value intact, no leftover temporaries after failure, and twenty concurrent writers each producing a
complete file.

### High — the equity dataset publisher is no longer single-symbol

`HistoricalEquityDatasetService` took its universe from `QUANTDESK_EQUITY_RESEARCH_SYMBOLS`,
defaulting to the four index ETFs, and one symbol failing no longer abandons the rest.

### New finding, discovered while fixing the above, and also fixed

**The C# equity dataset publisher was producing datasets the research plane could not read.** It ran
every six hours in production and its output was structurally unusable:

* it requested `feed=iex`, while the research loader rejects any manifest whose feed is not `sip`;
* its manifest had no `feed` or `adjustment` fields at all, and the loader requires both;
* it wrote `latest-spy-manifest.json`, while the loader opens
  `latest-spy-1day-sip.manifest.json`.

So the repository had two parallel, incompatible acquisition paths for the same data, and the
research that actually ran was fed entirely by the Python downloader. The C# service was consuming
API quota to produce files nothing opened.

`HistoricalDatasetManifest` now carries `Feed` and `Adjustment`; `AlpacaHistoricalStockBarClient`
takes both explicitly and validates them; and the publisher emits exactly the filename the research
plane reads. The feed defaults to `iex` because SIP requires a paid subscription — an operator with
one sets `QUANTDESK_EQUITY_RESEARCH_FEED=sip`. Choosing SIP silently would have been the wrong fix:
a dataset must never be mistaken for the other feed, which is why the manifest now records which was
used. `HistoricalEquityDatasetServiceTests` pins the exact filenames against the Python loader's
expectations.

### High — configuration is documented

`.env.example` now lists every variable the application reads, with the fourteen previously missing
ones added and explained. `QUANTDESK_AUTONOMOUS_MODE` is documented with all three values and the
warning that spot crypto's 50 bps round trip makes short-horizon crypto effectively inadmissible.
The entire experimental-authorization surface is documented for the first time.

### Still outstanding from the audit

Unchanged and still owed, in the original priority order:

1. `AutonomousPaperTradingService` still has zero tests — the untested money path.
2. `CryptoDiagnosticExecutionService` is still a 1,072-line god class; its durable lifecycle should
   be extracted so the autonomous lane can reuse it rather than reimplementing it a third time.
3. The 1,104 lines of dead Python remain, including the whole overfitting-control toolkit.
4. `CryptoMarketEvidence` is still the evidence type for equities and options.
5. `AlpacaTradingGateway` (404 lines) and `Program.cs` (388 lines) are still oversized.
6. The 16 duplicate `JsonSerializerOptions` instantiations remain.
7. Docs drift is still untracked.

## Mechanism research — 2026-09-01

Research only; nothing here was implemented. Every measurement uses the **discovery slice only**
(first 50% of sessions); validation and holdout remain unopened.

The catalogue committed on 2026-08-31 covers five equity-directional mechanisms, all already
tested and failed. Three of the five domains the original plan named had never been researched at
all — volatility risk premium, cross-asset information, and microstructure pressure. Those are
covered here, together with regime conditioning, universe breadth, and the overfitting toolkit.

**Headline: no new tradable mechanism was found. Two candidates looked viable and both turned out
to be artifacts.** Recording how they failed matters more than the failure, because both would have
passed a careless screen.

---

### 1. Microstructure pressure — researched, not viable

The 5-minute panel (95,011 bars, 500 sessions, 2024-08-30 to 2026-08-28) had never been used. It
is now characterised. Discovery slice: 250 sessions, 19,497 regular-session bars.

**Bar-to-bar autocorrelation is absent.** Largest magnitude across lags 1–12 is
`rho = -0.043` at 10 minutes; per-bar sd is 10.4 bps. The implied per-trade edge is
`0.043 x 10.4 = 0.45 bps` against a 9 bps equity round trip. Not marginal — two orders of
magnitude short.

**Time-of-day effects are multiple-testing noise.** The strongest buckets reach `t = +3.40`
(14:30) and `t = -3.39` (15:35), which looks impressive until you count the comparisons: these are
the extremes of 78 five-minute buckets, and the expected maximum |t| from 78 draws under the null
is already about 2.8–3.0. Magnitudes are 1.3–2.1 bps against a 9 bps hurdle regardless.

**Overnight drift is real but not tradable here.** Overnight (close→open) returns +4.01 bps/session
against intraday (open→close) +2.71 bps — annualised +10.1% versus +6.8%, consistent with the
documented overnight-drift literature. But `t = 0.86`, and capturing it costs a full round trip per
day against a 4 bps gross edge.

**The opening-continuation candidate failed on the arithmetic that matters.** Days are positively
autocorrelated within-session: `corr(first 30 min, rest of day) = +0.222`, and rest-of-day after a
top-quintile open averages **+32.3 bps** versus −2.4 bps after a bottom-quintile open — a 34.7 bps
spread that clears 9 bps with room to spare.

That framing is wrong, and it is worth naming precisely because it is the kind of number that gets
a strategy built. A quintile *spread* is a difference between two groups of days; it is not a
position anyone can hold. The implementable version — take the sign of the opening 30-minute move,
hold to the close — gives:

| Measure | Value |
| --- | --- |
| gross mean | **+7.94 bps/session** |
| sd | 92.09 bps |
| t-statistic | +1.36 |
| net of 9 bps round trip | **−1.06 bps/session** |
| net annualised | −2.67% |
| net Sharpe | −0.18 |
| edge over always-long | +3.38 bps gross |

Not significant before costs, and negative after them. **Verdict: rejected.**

### 2. Cross-asset information — researched, artifact

Lead-lag across the four ETFs looked strong at first: every one of the 16 ordered pairs is
negatively autocorrelated, from −0.073 to −0.210, strongest `DIA → QQQ` at −0.210.

Uniform negative lag-1 correlation of that size across *all* pairs, including each asset against
itself, is far larger than daily US equity data supports (typically −0.02 to −0.05). That
implausibility is the finding, so the data was verified rather than the signal trusted:

* row count matches the manifest (1,965), hashes verify, timestamps strictly increasing;
* no duplicate ET dates;
* SPY closes 243.12 → 769.35, **+216%** over the window, which is correct;
* the largest daily moves are the real COVID crash days (2020-03-16 −10.78%) and 2025-04-09 +10.50%.

The data is clean. The correlation is a **volatility artifact concentrated in the March 2020 crash**:

| Sample | SPY lag-1 autocorrelation |
| --- | --- |
| full history | −0.126 |
| second half only | −0.051 |
| excluding the largest 1% of days | **−0.040** |

Two-thirds of the apparent signal lives in about ten days of extreme stress. At the de-crashed
−0.040 the predicted move for a one-sd prior day is roughly 4.8 bps, below the 9 bps hurdle before
averaging over ordinary days.

A second correction: the first pass computed an "implied edge" of 35.56 bps by multiplying rho by
the daily sd. That is the regression-predicted response to a one-sd input, not a per-trade
expectancy, and it overstates the opportunity substantially. **Verdict: rejected as a crash
artifact.**

### 3. Volatility risk premium — cannot be researched yet

This is the domain with the strongest prior in the literature and it is completely blocked.

VRP is implied volatility minus causally expected realised volatility. Realised volatility is
available and characterised — SPY 21-day realised over discovery: mean **18.13%**, range
5.11%–87.34%, which is a healthy spread of regimes to work with.

Implied volatility is not available. `*option*` in the research data root returns **zero files**.
`AlpacaOptionContractClient`, `AlpacaHistoricalOptionBarClient`, and
`AlpacaLatestOptionQuoteClient` all exist and are tested, and `OptionResearchDatasetExporter` can
publish a hashed dataset — but no export has ever been run, because no session has held
credentials.

**This is the single highest-value blocked research item.** Unlike the equity families, VRP does
not depend on finding dispersion in four correlated ETFs; it is a different risk premium with a
different counterparty. It cannot be assessed at all until an option dataset exists.

### 4. Regime conditioning — researched, not worth its multiplicity

SPY next-day return conditioned on prior 20-day volatility tercile (discovery):

| Regime | Mean | sd | t | n |
| --- | --- | --- | --- | --- |
| low-vol | +1.36 bps | 73.0 | +0.33 | 318 |
| mid-vol | +6.01 bps | 107.2 | +1.00 | 317 |
| high-vol | +6.81 bps | 200.6 | +0.61 | 327 |

Best-minus-worst spread is 5.45 bps/day, but **no tercile is individually significant** and the
highest t across three is 1.00. Selecting the best of three regimes triples the effective
comparison count, so the spread would need to clear both the cost hurdle and that multiplicity.
It clears neither. **Verdict: volatility-tercile regime conditioning adds nothing here.** A regime
filter with an independent economic motivation might; a filter chosen by scanning terciles will not.

### 5. Universe breadth — quantified, and it helps less than previously claimed

Earlier notes in this handoff implied widening the universe was close to a silver bullet. It is
not, and the correction matters for planning.

Effective independent bets from `N` assets at average correlation `rho` is
`N / (1 + (N-1) x rho)`. Holding the per-asset gross edge fixed, Sharpe scales with the square root
of that, and required years scale inversely with its square:

| Assets | rho | Effective bets | Relative Sharpe | Years needed @ Sharpe 0.6 |
| --- | --- | --- | --- | --- |
| 4 (today) | 0.877 | 1.10 | 1.00x | 7.5 |
| 11 sector ETFs | 0.60 | 1.57 | 1.19x | 5.3 |
| 30 names | 0.40 | 2.38 | 1.47x | 3.5 |
| 30 names | 0.25 | 3.64 | 1.82x | 2.3 |

The four index ETFs supply **1.10 effective bets** — essentially one. Eleven sector ETFs at 0.60
correlation give 1.57, cutting the requirement from 7.5 years to 5.3. That is a **1.4x improvement,
not the 3x implied earlier**; only a 30-name universe at 0.25 correlation reaches 3.3x.

The practical reading is still favourable, just for a narrower reason: 7.8 years of history are
available, so today's universe is *marginally* able to prove a Sharpe-0.6 strategy while sector
ETFs would prove one *comfortably*. Breadth moves the position from marginal to comfortable; it
does not manufacture an edge that is not there.

Minimum provable Sharpe given the data that exists: **0.83 on discovery, 0.59 on full history.**
Any four-ETF result claiming to clear those is more likely overfit than real.

### 6. The dead overfitting toolkit — characterised, and one module corrected

`evaluation/pbo.py` is not a stub. It is a working 58-line implementation of Probability of
Backtest Overfitting, and its input is exactly the shape this repository already produces: the 14
families' daily returns over 982 discovery sessions form the `(T, N)` matrix it expects. It answers
the precise question the equity campaign needs — *is the best of my 14 families genuinely best, or
is it selection noise?*

A first single-seed test returned an identical 0.25 for pure noise and for a planted edge, which
looked like a broken discriminator. Testing across 12 seeds shows that was a misleading draw:

| Input | Mean PBO | Correct value |
| --- | --- | --- |
| pure noise, 14 families | 0.370 | ~0.50 |
| one obvious edge planted | 0.099 | ~0.00 |

It **does** discriminate. It is, however, **biased low** — pure noise should return roughly 0.5 and
returns 0.370, so it under-reports the probability of overfitting. The module's own comments admit
why: it implements a leave-one-partition-out jackknife rather than full combinatorially symmetric
cross-validation. Wired in as-is it is usable as a relative ranking signal between families, but its
absolute value must not be quoted as a probability, and it should not be presented as CSCV.

---

### What this changes about priorities

1. **Volatility risk premium is now the most valuable unexplored direction**, and it is blocked
   solely on exporting an option dataset. It does not depend on ETF dispersion, which is the
   constraint that killed everything else.
2. **Microstructure and cross-asset are closed** for this universe and these costs. Both were
   measured, both failed, and both are recorded so they are not re-attempted.
3. **Universe breadth is worth doing but is not transformative** — 1.4x from sector ETFs, not 3x.
4. **PBO should be wired in before any further family search**, with its low bias stated. It is the
   cheapest available guard against exactly the failure mode this campaign keeps producing.
5. Two artifacts nearly became strategies in one session. Both were caught by asking whether the
   implementable position matched the measured statistic, and whether a correlation survived
   removing the crash. Those two checks belong in the screen itself.

## Research failures that must remain knowledge

- Broad frozen BTC validation: all 32 comparisons failed.
- ETH/USD transfer: 107 trades; mean net `+14.320408 bps`; adjusted lower bound `-369.440078 bps`; Sharpe `0.084252`; rejected.
- Published one-to-eight-week BTC momentum confirmation: all candidates failed adjusted confidence/Sharpe requirements.
- Preregistered BTC 4h/12h/24h/48h trend-state campaign: all candidates failed; net expectancy stayed near the conservative 60 bps cost.
- Equity campaign `US_EQUITIES_RESEARCH_001`: all 20 preregistered candidates failed; holdout remained unopened.
- New equity relative-strength/reversal family: all six discovery candidates failed; validation and holdout remained unopened.

Persist the newest equity family failure in the typed hypothesis memory. Do not repeat the same parameter neighborhood without genuinely new evidence.

## Everything still not done

Superseded. Every item that was listed here is now in
**"EVERYTHING NOT DONE — consolidated register"** below, which is the single authoritative list.
The MLeg admission items became section E, the lifecycle items section D, the evidence and
publication items section F, and the runtime and final-trade items section H. Nothing was dropped
in the merge.

The immediate verification debt that used to head this section was cleared on 2026-08-31; see
"Verification completed 2026-08-31" above for the counts.

## Exact next commands

Run all .NET verification inside a single container invocation (see the method caveat above):

```powershell
docker run --rm -v "${PWD}:/src" -w /src mcr.microsoft.com/dotnet/sdk:10.0 bash -lc "dotnet restore QuantDesk.slnx && dotnet build QuantDesk.slnx --no-restore && dotnet test QuantDesk.slnx --no-build --nologo"
```

Confirm five `Passed!` lines. Four means a suite was silently skipped, not that everything passed.

Python:

```powershell
Set-Location research/python
uv run --frozen ruff check .
uv run --frozen mypy src
uv run --frozen pytest -q
Set-Location ../..
```

Suggested next work, in order:

1. Give `OptionResearchDatasetExporter` a trigger: a hosted worker that reads the SPY daily manifest
   published by `HistoricalEquityDatasetService`, passes its SHA-256 as the underlying dataset hash,
   and exports a contract snapshot plus a bar dataset on a schedule.
2. Verify `AlpacaOptionContractClient` against a real authenticated PAPER response, and only then
   consider moving the option feed off `UNVERIFIED`. Expect the adjusted-contract and standard-size
   guards to be the first to fire against live data; treat a rejection as information, not a bug to
   suppress.
3. Add option quote/spread snapshots — bars cannot price a spread's execution economics.

Do not start by opening validation/holdout or enabling autonomous execution.

## EVERYTHING NOT DONE — consolidated register

The single authoritative list of outstanding work. Sections above give the reasoning and evidence
behind each entry; this is the checklist. Rows marked **Closed** were completed after the register
was first written and are kept, rather than deleted, so the next session can see what was answered
and how. Items are grouped by what they block, and ordered within each group by risk removed per
unit of effort.

Status as of 2026-08-31: **no strategy qualifies, no autonomous strategy order has ever been
submitted, autonomous execution is disabled, and no session has ever held Alpaca credentials — so
nothing in this repository has yet contacted the live venue or placed a trade.**

---

### A. Blocking a first trade

| # | Item | Why it blocks |
| --- | --- | --- |
| A1 | **No Alpaca credentials in any session.** Every result recorded here is against stubbed responses. | Nothing can reach the venue. This is the single hard blocker. |
| A2 | **Closed.** `OptionExecutionCoordinator` connects the compiler to `MultiLegExecutionLifecycle`, and `AutonomousPaperTradingService.ExecuteOptionOpportunityAsync` drives it. Observed end to end against a recording broker: `state=EntrySubmitted maxLoss=327.00 debit=3.27`. | — |
| A3 | **Closed.** `OpportunityRouter` classifies the symbol first and the service takes the spot or option lane accordingly. | — |
| A4 | **No strategy qualifies.** Four families clear discovery; none beat passive equal-weight out-of-sample. | `NO_TRADE` is currently correct. Forcing a trade would be worse than not trading. |
| A5 | **Closed.** `SpotExecutionStore` and `SpotExecutionLifecycle` give the spot lane reservation-before-POST, an atomic submission claim, ambiguous-submit recovery by client-order-ID, a restart-surviving exit, and completion only on zero exposure. Writing the test also found a real race: two evaluations could produce two records for one symbol, because the broker-position check cannot see an order that is not yet a visible position. | — |

### B. Untested money path

| # | Item |
| --- | --- |
| B1 | **Closed.** `AutonomousPaperTradingServiceTests` covers the orchestration in 10 tests. Testing it required introducing `IMarketEvidenceProvider`, which is a design improvement rather than a test workaround — the money path had no seam. |
| B2 | **Closed, and it was worse than untested.** `ExecutionAdmissionPolicy` had tests but *no production caller* — a class that reads like the system's central admission gate, DI-registered and never invoked. Its rules also restated the readiness check a second time, and the copy was wrong for closes. The rule now lives once on `FullSystemReadinessSnapshot.IsReadyFor`, the policy adds only reason codes, and the diagnostic lane calls it. |
| B3 | **Closed.** `CryptoFeeScheduleTests` covers it. |
| B4 | Still thin: exercised through `AutonomousPaperTradingServiceTests` via `IMarketEvidenceProvider`, with no tests on the concrete provider's own parsing. |
| B5 | `BlackScholes`, `OptionChainValidator`, `ExecutionJournalReplay`, and `PythonResearchContractReader` are each referenced by exactly one test file. |
| B6 | Duplicate-prevention is proven only across sequential restart, never with two store/lifecycle instances racing. |
| B7 | Atomic store behaviour under interrupted writes and corrupted JSON is unproven for the MLeg store. |

### C. Research

Items marked **researched** were measured on 2026-09-01; see "Mechanism research — 2026-09-01"
above for the numbers. Researched does not mean implemented — it means the domain was investigated
and a verdict recorded, so it is not re-attempted blindly.

| # | Item | Status |
| --- | --- | --- |
| C1 | Widen the research universe. Four ETFs at 0.877 correlation supply 1.10 effective bets. | **Researched.** Quantified: 11 sector ETFs give 1.4x, not the 3x implied earlier; 30 names at 0.25 give 3.3x. Still worth doing. Needs credentials. |
| C2 | 1,104 lines of dead Python, including the overfitting toolkit. | **Partly researched.** `pbo.py` characterised: works, discriminates, but biased low (0.370 on noise vs correct ~0.5) and is a jackknife, not CSCV. Wire it in with that caveat stated. `purged_cv.py` and `walk_forward.py` still uncharacterised. |
| C3 | Option quote/spread snapshots for research. | Open. Blocked with C7 on the same missing dataset. |
| C4 | Regime-conditioned expectancy. | **Researched — negative.** Volatility terciles give no significant conditional edge (max t = 1.00) and selecting the best of three triples multiplicity. A filter with independent economic motivation might work; one chosen by scanning terciles will not. |
| C5 | Mechanism-disagreement abstention. | Open. Lower value than it appeared: with every mechanism now rejected there is nothing left to disagree. Revisit once two mechanisms both survive. |
| C6 | Formal mechanism catalogue. | **Done** — `MECHANISM_CATALOGUE` with falsification rules, persisted immutably. |
| C7 | SPY volatility-risk-premium family. | **Researched — blocked, and now the highest-value direction.** Realised vol is available (mean 18.13%, range 5.11–87.34%); implied vol is not. Zero option datasets exist. Unlike every other family it does not depend on ETF dispersion. |
| C8 | SPY directional debit-vertical family. | Open, and correctly gated: it requires an independently qualified underlying signal, and none exists. |
| C9 | Persist rejected hypotheses. | **Done** — `persist_rejected_families` writes typed rejections. |
| C10 | Execution-mode-aware crypto cost scenarios. | **Done** — conservative-stress, taker, maker, and observed-realised kept distinct, with observed costs unable to relax qualification. |
| C11 | Historical expired-contract discovery against Alpaca. | Open. Blocked on credentials. |
| C12 | Option feed remains `UNVERIFIED`. | Open, but now answerable in one command: `quantdesk option-preflight` reports every option data path read-only. Still blocked on credentials to run it. |

**Closed by research, do not re-attempt without new evidence:**

| Domain | Verdict |
| --- | --- |
| Microstructure pressure | Rejected. 5-min autocorrelation ~0 (implied 0.45 bps vs 9 bps hurdle); time-of-day effects are the extremes of 78 buckets; opening continuation is +7.94 bps gross (t=1.36) and **−1.06 bps net**. |
| Cross-asset lead-lag | Rejected. Apparent −0.21 correlation collapses to −0.040 once the March 2020 crash is excluded. Data verified clean; the signal was a volatility artifact. |
| Volatility-tercile regime conditioning | Rejected. No tercile individually significant. |

### D. Multi-leg options lifecycle

| # | Item |
| --- | --- |
| D1 | **Implemented:** persist nested parent/leg broker snapshots for entry and exit, including per-leg broker identities. |
| D2 | **Implemented fail-closed:** reported nested legs must map exactly to requested OCC symbols and ratios; broken or incomplete final fills enter reconciliation failure. |
| D3 | **Implemented:** attribute signed internal position quantity per OCC leg from persisted broker leg fills. |
| D4 | **Implemented:** reconcile owned parent orders, every persisted leg, broker leg positions, and internal leg inventory; missing final nested-leg truth fails closed. |
| D5 | **Partial:** stale unfilled acknowledged parent orders are bounded-cancelled after the configured timeout with no replacement post. Repricing remains open pending a validated replacement-price policy. |
| D6 | **Implemented:** idempotent, PAPER-protected emergency options flatten derives broker-truth leg exposure, uses deterministic close IDs with lookup-before-submit, and verifies flat afterwards. |
| D7 | **Implemented:** bounded lookup of an ambiguous submission becomes terminal `SubmissionUnresolved`; the original client ID is preserved and no retry POST is allowed. |
| D8 | Typed timeout, broker-unavailable, permission, contract-expired, broken-leg, and reconciliation-failure states. |
| D9 | **Implemented:** authenticated `GET /api/options/recovery` exposes recovery liveness, last cycle/error, and nonterminal count. Broader readiness remains governed by the existing fail-closed system ledger. |
| D10 | **Implemented in-process telemetry:** secret-free MLeg transition counter records only source state, target state, and terminal status. Metrics-exporter/runtime collection evidence remains open. |
| D11 | **Implemented in hosted-service test:** recovery advances a seeded nonterminal record without any parent or single-leg submission. Production-container runtime evidence remains external. |

### E. Admission and risk for options

| # | Item |
| --- | --- |
| E1 | Require live account health and an adequate options level at admission, per requested strategy. |
| E2 | Query and verify every selected contract as active and tradable before reservation. |
| E3 | Query open parent/leg orders and all selected-contract positions before entry; block on unexplained exposure. |
| E4 | Integrate `RiskGovernor`, capital reservation, and leg-aware defined maximum loss before lifecycle reservation. |
| E5 | Include option spread, slippage, per-contract fees, assignment/exercise, and buying-power effects in admission economics. |
| E6 | Persist the evidence and artifact identity that authorised the execution, and refuse to create a record unless artifact, option candidate, risk decision, and broker contract set all match. |

### F. Evidence publication and artifact contracts

| # | Item |
| --- | --- |
| F1 | Prove every required gate `R0 R1 R2 R3 R4 R5 R6 R7 R11 R12` is produced by a real evaluator for a qualifying candidate. |
| F2 | Prove the worker publishes a complete rule-based bundle atomically after one-time independent validation. |
| F3 | Extend artifact semantics for equity and multi-leg options; the current directional contract is insufficient. |
| F4 | Carry exact option selection, leg ratios, entry/exit policy, maximum loss, cost model, and contract snapshot identity into the artifact. |
| F5 | Make C# reject any mismatch among forecast, artifact, selected contracts, runtime strategy, and risk reservation. |
| F6 | Prove a restart does not rerun or retune an opened independent-validation campaign. |
| F7 | Build `current-contracts.json` only after all evidence is complete and valid. |

### G. Architecture and technical debt

| # | Item |
| --- | --- |
| G1 | **Extraction complete: 1,072 lines to 762.** Command construction, admission policy, exposure arithmetic, failure classification, and finally the emergency-flatten sub-lifecycle now live in their own classes with 51 tests on logic that previously could not be reached. What remains open is the *reuse* half of the original item: the autonomous lane has its own `SpotExecutionLifecycle` rather than sharing one durable lifecycle with the diagnostic lane. Two implementations, both tested, still one more than necessary. |
| G2 | **Implemented:** the cross-asset contract is `DirectionalMarketEvidence`, decoupled from the crypto market-data client. |
| G3 | `AlpacaTradingGateway` (404 lines) owns account, asset, submit, lookup, orders, positions, cancel, replace, close, and multi-leg mapping, and grows with every asset class. |
| G4 | `Program.cs` (388 lines) mixes registration with inline endpoints and business defaults. |
| G5 | **Implemented:** production contract readers, writers, stores, and manifests share canonical web/indented JSON options. CLI display formatting remains intentionally local. |
| G6 | **Implemented for the common bar manifest:** C# and Python pin the same camel-case `dataFile`/`rowCount` contract with tests. Option-specific manifest semantics remain a separate contract. |
| G7 | **Implemented:** pricing `OptionQuoteSnapshot` carries only quote fields; authenticated `OptionRiskSnapshot` holds IV/Greeks separately and marks missing values stale rather than fabricated. |
| G8 | **Closed.** `BrokerExposureAttributor` builds the attribution that did not exist, from the lanes' own durable stores: orders attribute exactly by deterministic client order ID, positions by symbol against nonterminal records. Entry now halts on *unattributed* exposure, and abstains when the instrument it wants is already claimed by a lane. One lane holding SPY options no longer stops another trading BTC. The asymmetry is documented: a hand-placed position in a symbol a lane already trades is absorbed rather than flagged; everything in an unclaimed symbol is still foreign. A lane registered without a claim source reports its exposure as foreign, which halts entry — failing closed. |
| G9 | **Implemented:** `AlpacaHistoricalStockBarClient` rejects null/missing payloads, unrequested symbols, malformed bars, and conflicting data rather than silently filtering them. |
| G10 | **Implemented:** enabled autonomy requires an explicit configured execution symbol; BTC/USD remains only the disabled research-data fallback. |
| G11 | `crypto_direction.py` is 810 lines and serves a lane the cost analysis has ruled out. |
| G12 | 74 files in `Docs/`; at least `Docs/AUTONOMOUS_TRADING_CONNECTION_AUDIT.md` predates the routing work. Docs drift is caught by no test. |
| G13 | Test-name-to-source mapping is unreliable, so coverage gaps cannot be checked mechanically. |
| G15 | **Strict `mypy` covers `src` only.** `mypy src` reports "no issues found in 85 source files"; `mypy .` finds 51 errors, all of them in `tests/` (mostly missing annotations). The claim "mypy strict, clean" is true and narrower than it sounds — the test suite is not type-checked. |
| G16 | **Closed.** `.github/workflows/python.yml` runs `uv sync --frozen`, ruff, `mypy src`, and pytest on every push and pull request. `--frozen` makes an out-of-date `uv.lock` a CI failure rather than a silent re-resolve. Every step verified green locally before committing. |
| G14 | **Implemented:** the diagnostic minimum quantity is the named `DiagnosticExecutionOptions.MinimumCryptoQuantity` invariant, not a composition-root magic literal. |

### Option clients against real venue shapes — 2026-09-01

Every option client had been written and tested against payloads a previous session invented. The
fixtures matched the parsers, so the tests passed, and the pair proved nothing about Alpaca. Testing
them against Alpaca's documented shapes instead found four defects, all of which would have fired on
the first live call.

| # | Defect | What it would have done |
| --- | --- | --- |
| 1 | **Implied volatility was read from the wrong place.** The snapshot client looked for `implied_volatility` inside `greeks`; Alpaca sends `impliedVolatility` beside it. The absent property deserializes to an `Undefined` `JsonElement`, and `GetString()` throws on one of those rather than returning null. | Every option risk snapshot throws on the first real response. Not a degraded reading — an exception out of the risk lane. |
| 2 | **A quote stamped ahead of the caller's clock was refused outright.** The freshness test required the venue timestamp to be strictly in the past. | The venue stamps to the nanosecond from its own clock. A local clock trailing by milliseconds marks every healthy quote stale and silently refuses every spread — an entire lane disabled by ordinary NTP drift, with nothing in the logs but "stale". Now a bounded skew is tolerated and a large one is still refused. |
| 3 | **One non-standard contract destroyed the whole chain.** An adjusted root, a non-standard multiplier or deliverable size, or an unrecognised exercise style threw and failed the entire acquisition. | A single adjusted contract costs every standard contract beside it — on a real chain, nearly all of them. Now those are *excluded with a stated reason* and carried on `OptionContractQuery.Excluded`, while genuine self-contradiction (strike, expiration, or type disagreeing with the OCC symbol) still fails the whole query, because that means the feed cannot be trusted. |
| 4 | **Adjusted contracts failed OCC parsing outright.** `OccOptionSymbol` allowed only `[A-Z]` in the root, but a corporate action is encoded as a *numbered* root — `SPY1`, `AAPL1` — with the underlying still `SPY`. | Every adjusted contract was reported as "an invalid option symbol", which is wrong twice: the symbol is valid, and callers reading an unparseable symbol as a corrupt feed discard the whole chain over one ordinary contract. It also made the exclusion path in defect 3 unreachable for the exact case it was written for. Root is now `[A-Z][A-Z0-9]{0,5}`; the trailing fifteen characters are fixed-width, so a digit-bearing root stays unambiguous. |
| 5 | **Venue error bodies were discarded.** Every market-data client called `EnsureSuccessStatusCode()`. | The first live call with an unentitled account raises "Response status code does not indicate success: 403 (Forbidden)" and nothing else. Alpaca puts the actual explanation in the body. All ten market-data call sites now report status, endpoint, and the venue's own code and message — and a test asserts the API secret never appears in that message. |

The distinction drawn in defect 3 is the general lesson, and it is worth stating on its own: **a
response that contradicts itself and a contract this system cannot price are different failures.**
The first means the feed is untrustworthy and everything derived from it must be discarded. The
second is an ordinary fact about a real option chain. Answering both by throwing looked strict and
was merely brittle.

An empty contract snapshot now says which of the two happened — "the venue returned none" versus
"all N returned contracts were excluded, first: ..." — because those call for opposite responses and
the count alone cannot distinguish them.

`quantdesk option-preflight` now exercises all four option paths against the live venue, read-only,
and prints what each returned. A stage that fails does not stop the ones that do not depend on it —
learning that contracts resolve but quotes are unentitled is a different situation from learning that
nothing works, and finding out one call at a time wastes the scarcest thing here, which is attempts
against a venue nobody has reached yet. The CLI's `HttpRequestException` handler was also printing a
generic "could not be reached" line, which would have thrown away the venue diagnostics the clients
now carry.

Defect 4 is worth noting for how it was found: the preflight fixture used a realistic adjusted symbol,
the test failed, and the first reading was that the fixture was malformed. It was not. That is the
same pattern as the other four — testing against what the venue actually sends, rather than against
what the parser already expects.

**First real venue contact — 2026-09-01.** The preflight was run against
`paper-api.alpaca.markets` with the placeholder credentials still in `.env`. It reached Alpaca and
returned `contract discovery: Failed — Alpaca /v2/options/contracts failed with 401 Unauthorized:
unauthorized`, with the three dependent stages `Skipped`. That is the intended shape of a first
contact: one run, the venue's own answer, and no guessing about which stage failed. Earlier notes in
this file saying nothing here has ever contacted Alpaca are superseded — read-only, no order placed.

**Credential-shape diagnosis.** A malformed key and a revoked key both come back `401 unauthorized`,
and the difference decides whether to regenerate a key or go looking at account permissions. The first
real credentials tried in this repository were an Alpaca *account number* pasted into the key-ID field
— which Alpaca's dashboard shows a few centimetres from the API key. `AlpacaCredentialShape` now adds
a sentence naming that when the venue refuses. It is advisory and runs only after a refusal: Alpaca can
change key formats at will, and a shape rule that *blocked* a request would eventually reject working
credentials, which is a far worse failure than the opaque one it fixes.

### Two model-plane improvements, and the number that ends the argument — 2026-09-01

**Improvement 1 — stop selecting, start combining.** The campaign ranked fourteen families and took
the winner. That is *pure selection bias*: with fourteen candidates on a 3.9-year slice the winner is
largely the luckiest, which is precisely what a probability of backtest overfitting of 0.50 has been
reporting. Two ensembles now exist whose membership is decided by a structural attribute declared
before any result is seen — `ensemble-all` takes every hypothesis family, `ensemble-directional`
takes the long-only ones. Neither consults a result, so neither can be overfitted.

Across the 28 held-out paths:

| family | median Sharpe | beats benchmark | selection bias |
| --- | --- | --- | --- |
| **ensemble-directional** | **0.97** | **75%** | none — membership is structural |
| defensive-low-vol-63d | 0.91 | 71% | chosen after seeing results |
| ensemble-all | 0.90 | 75% | none |
| best single trend family | 0.90 | 64% | chosen after seeing results |

The blend beats the best cherry-picked family *without making a choice*. The gross book is
renormalised after averaging, because dollar-neutral and long-only constituents partly cancel and an
un-renormalised ensemble would run a smaller book and report a flattered risk-adjusted return for
that reason alone.

**Improvement 2 — the multiple-testing control that was claimed but absent.** The campaign docstring
stated the deflated Sharpe ratio was "reported alongside as the explicit multiple-testing
diagnostic". It was computed nowhere. `deflated_sharpe.py` existed and was never imported — a stated
control that did not exist in the code path, which is worse than no control because it invites
belief. It is now computed for every family.

**And it settles the question.** Deflating for sixteen trials on the discovery slice:

| family | Sharpe | DSR |
| --- | --- | --- |
| vol-scaled-trend-126d | 0.604 | **0.181** |
| ensemble-directional | 0.552 | 0.155 |
| equal-weight-benchmark | 0.457 | 0.115 |

**Nothing survives at DSR > 0.95. The best family has an 18% probability its true Sharpe is even
positive** once the search width is accounted for. That is the honest state of the evidence, and it
is consistent with everything else measured: PBO 0.50, 1.12 independent bets, 3.9 years of discovery.

**The conclusion a veteran would draw.** The models are not broken and the strategy library is not
missing a family — it already contains the highest-evidence mechanisms in the literature. The
evidence base is simply too thin to distinguish any of them from the luckiest of sixteen draws. The
binding constraints, in order:

1. **Universe.** 0.960 correlation, 1.12 independent bets. This caps everything, and widening it to
   the sector ETFs is the single highest-value change available.
2. **Sample.** 3.9 years of discovery cannot support sixteen trials. Deflation is unforgiving here
   and correctly so.
3. **Cost.** 80 bps measured per crypto round trip kills every high-turnover family outright.

Trading anything from this evidence base would be trading a coin flip with a known toll attached.

### Veteran audit: why the system could not find an edge — 2026-09-01

Going through the application end to end looking for the reason nothing predicts. The signal
construction is clean — `shift(1)` applied once in `build_weights`, so every weight uses closes
through *t−1* only; the loader refuses anything but SIP bars with `adjustment=all`, so dividends are
in. The defects are not in the plumbing. They are in the **evaluation design and the universe**, and
both are fatal in their own right.

**Defect 1 — the split handed every crisis to discovery and none to the tests.**

| phase | sessions | ann return | Sharpe | max DD |
| --- | --- | --- | --- | --- |
| discovery | 982 | 11.33% | 0.48 | **−34.8%** |
| validation | 491 | 25.30% | **1.56** | −11.6% |
| holdout | 492 | 22.62% | 1.29 | −20.3% |

The chronological 50/25/25 put the COVID crash and the 2022 bear market in discovery and left the
calmest stretch of the sample as the out-of-sample test. A trend or defensive family exists to give
up upside for protection; in a window with no crisis it pays the premium and collects nothing. A
capped-at-zero trend strategy can only be flat or long in a bull market, so underperformance there
is the design working, not the signal failing. **The "does not beat equal weight" gate was close to
tautological in that window.**

**Defect 2 — the universe is one asset wearing four hats.** Mean pairwise correlation of
SPY/QQQ/IWM/DIA is **0.960**, not the 0.859 previously recorded, giving roughly **1.12 independent
bets**. Cross-sectional strategies rank assets against each other; with 1.12 effective bets there is
almost no dispersion to rank. That is why every `xs-` family is negative, and no model choice can
repair it.

**The fix, and what it uncovered.** `backtest/combinatorial.py` evaluates a schedule across every
combination of held-out blocks — 8 blocks, 2 held out, 28 paths of ~449 sessions each — reporting a
distribution instead of one path. Judged that way:

| family | median Sharpe | beats benchmark |
| --- | --- | --- |
| **defensive-low-vol-63d** | 0.91 | **71%** |
| equal-weight-benchmark | 0.91 | — |
| vol-scaled-trend-252d | 0.90 | 64% |
| ts-trend-252d | 0.88 | 46% |
| every cross-sectional family | ≤ 0.35 | ≤ 25% |

**`defensive-low-vol-63d` beats the benchmark on 71% of paths** — the low-beta premium of Frazzini
and Pedersen. The single-window test had reported it as passing on Sharpe alone and losing on return;
across 28 regimes it is the only family clearing a two-thirds threshold. **The evaluation method, not
the strategy library, was hiding the one candidate the system had.**

Two cautions kept in view: dispersion is wide (0.56–0.76 across paths) and the worst path is negative
for every family including the benchmark, so this is a tilt worth testing, not a discovered edge. And
71% on 28 overlapping paths is far from proof — the paths share data.

**A methodological note worth keeping.** The first embargo attempt used the full lookback (252 days)
against 245-session blocks and removed every observation. The guard refused rather than returning an
empty result that would have read as "no edge". The correct embargo here is the holding period, not
the lookback: **these families fit no parameters**, so the leakage purging normally defends against
cannot occur — nothing is trained that could have seen the test period. What must be removed is the
stale-weight boundary at the start of a held-out block.

### Every model run, and what they actually say — 2026-09-01

All fourteen families were run on the committed SIP daily ETF panel (1,965 sessions), covering the four
highest-evidence types in the reference corpus: cross-sectional momentum, time-series trend,
volatility-scaled trend, and defensive low-volatility.

**Discovery ranked exactly as the literature predicts.** `vol-scaled-trend-126d` first at 4.773 net
bps/day, Sharpe 0.604 — time-series momentum with volatility scaling, which is Moskowitz/Ooi/Pedersen
combined with Moreira/Muir. Four families passed every gate.

**Validation reversed it.** Out of sample, *every* trend family loses to buy-and-hold equal weight:

| family | net bps/d | Sharpe | verdict |
| --- | --- | --- | --- |
| **equal-weight-benchmark** | **8.948** | **1.389** | the thing to beat |
| defensive-low-vol-63d | 8.861 | 1.431 | passes on Sharpe alone, not on return |
| ts-trend-63d | 7.133 | 1.309 | loses to benchmark |
| vol-scaled-trend-126d | 5.514 | 1.116 | loses to benchmark |
| every cross-sectional family | ≤ 2.5 | — | fails multiple gates |

**PBO = 0.500 on validation — the coin-flip maximum.** The families that topped discovery are not the
ones that do best out of sample, which is the signature of selection noise rather than edge. This is
precisely the failure Harvey/Liu/Zhu and Hou/Xue/Zhang describe.

**The decisive number is turnover, once cost is measured rather than assumed.** What round-trip cost
does each family's out-of-sample edge survive?

| family | turnover/day | breakeven cost |
| --- | --- | --- |
| equal-weight-benchmark | 0.002 | **4,479 bps** |
| defensive-low-vol-63d | 0.004 | 2,220 bps |
| vol-scaled-trend-126d | 0.011 | 506 bps |
| ts-trend-63d | 0.038 | 193 bps |
| xs-momentum-63d | 0.059 | 48 bps |
| xs-reversal-3d | 0.372 | **7 bps** |

The measured venue cost is **80 bps per round trip**. Everything from `xs-momentum-63d` down is dead on
arrival — its edge cannot pay the toll. The families that survive are the ones that barely trade.

**So "use the models to stop losing money" has an answer, and it is not the one it looks like.** The
models are running, they are the right families, and their verdict is that the best risk-adjusted
allocation available in this data is a near-zero-turnover hold — 22.55% annualised at Sharpe 1.389 with
turnover of 0.002/day. Every attempt to trade around it underperforms it out of sample, and at 80 bps a
round trip the high-turnover families are not close.

The diagnostic lane loses money because it trades ~1.0 turnover every two minutes for no signal at all.
It has now proven the execution path four times over; continuing to run it is paying 80 bps a cycle for
information already obtained.

### Research families re-examined against the measured 68 bps floor — 2026-09-01

| family | charges | validated against a real fill | verdict |
| --- | --- | --- | --- |
| `crypto_direction` | 60 bps (schedule says 50) | **yes — 68 bps measured** | `NO_TRADE`, and by a wider margin than believed |
| `equity_campaign`, `equity_portfolio_strategies`, `equity_relative_strength`, `strategy_ensemble`, `prospective_campaign` | 5 bps BASE / 10 STRESS / 20 SEVERE | **no — no equity has ever been traded** | conclusions rest on an unvalidated assumption |

**Crypto: corrected, verdict unchanged.** `CRYPTO_TAKER_ROUND_TRIP_BPS_MEASURED = 68.0` records the
measurement with its provenance, and `crypto_direction.run_experiment` now charges it by default instead
of 60. The family cannot be re-run today because **no crypto dataset exists** — only
`US_EQUITIES_RESEARCH_001` — so this is a correction that binds the next time crypto data is acquired.
Raising the hurdle only strengthens an existing `NO_TRADE`.

**Equity: the assumption is reasoned but untested, and crypto just showed how that fails.** The 5 bps
derivation is sound and cited — penny spreads on $240–650 ETFs are 0.15–0.4 bps per crossing, regulatory
fees are sub-basis-point, commission is zero, so ~2 bps honest and 5 bps charged is conservative. The
danger is not the model. On crypto the *model* was roughly right (50 vs 68) while the *measurement* was
six times wrong, because a cash charge never appears in a fill price or quantity. Alpaca equities have
exactly that shape: SEC and TAF fees are cash charges on sells.

**So the transferable rule, and the one that matters: P&L must be read from account equity, not from
fills.** That is now implemented for the diagnostic lane via `RealisedAccountPnl`, and it is the standard
any lane must meet before its results are believed.

**Outstanding and specific:** validate the equity cost ladder with a bounded live SPY round trip,
measuring account equity before and after exactly as the crypto measurement did. It could not be done in
this session — US equity regular hours are 13:30–20:00 UTC and the work reached this point at ~21:15 UTC,
an hour after the close.

### What is actually losing the money — 2026-09-01

**Not the strategies. There were none.** All 59 round trips came from the diagnostic lane, which has no
signal, no forecast and no model: it buys $10 of BTC, waits exactly 120 seconds, and sells. It is built
to pay the round-trip cost and report whether the machinery worked. Losing money there says nothing
about strategy quality, and 59/59 completed with zero failures.

**The real defect was that the system could not see what trading costs.** Three measurements of the
same 59 trades:

| source | today's total | per $10 round trip |
| --- | --- | --- |
| `grossPaperPnl` | −$0.67 | **11 bps** |
| `netPaperPnl` (fill-derived) | −$2.12 | **36 bps** |
| **broker account equity** | **−$4.04** | **68 bps** |

Gross omits the fee entirely. The fee-aware net catches the in-kind quantity loss but not the separate
`Coin Pair Transaction Fee (USD)` cash charge, which never appears in a fill price or quantity and so
**cannot be derived from fills at all**. Half the true cost was invisible to every figure the system
produced.

**The cost model was right the whole time.** `CryptoCostScenarios.FeeFloor` is 50 bps + spread ≈ 60,
`Base` ≈ 70; the account says 68. The research plane's `NO_TRADE` verdict on crypto was correct and is
now validated against broker truth for the first time. It was the *execution* plane claiming trading was
six times cheaper than it is — and that is the number a search would have optimised against.

`RealisedAccountPnl` now records account equity at reservation and again at reconciliation, so every
round trip carries what it actually cost, owing nothing to a fee model. First live measurement:

```
gross         -0.0066   ( 6.6 bps)
net (fills)   -0.0311   (31   bps)
account truth -0.0600   (60   bps)
```

**Strategy and model inventory, for the record.** C#: `AutonomousDecisionPipeline`, `ExpertCommittee`,
`CommitteeAllocator`, `DirectionalStrategyCompiler`, `CryptoDirectionalStrategyCompiler`. Python:
`crypto_direction`, `equity_campaign`, `equity_portfolio_strategies`, `equity_relative_strength`,
`strategy_ensemble`, `prospective_campaign`. **None has produced a qualifying artifact, and none placed
any of the 59 trades.** Nothing in that list is responsible for the losses; a 68 bps round trip at a
two-minute horizon is.

### Stress campaign 2 — restart recovery proven under process death — 2026-09-01

**Phase C, the test campaign 1 failed to actually run.** A diagnostic was driven to a live fill
(0.000127098 BTC), then the API container was killed outright and restarted:

```
entry filled 0.000127098  ->  API CONTAINER KILLED with the position live
api back 3s later
RECOVERED -> state=Complete  recon=Flat  exitFill=0.00012678
             gross=+0.00336  net=-0.02118
```

The durable record survived, the recovery worker resumed a half-finished lifecycle, the position was
closed and reconciliation came back `Flat`. That is the guarantee the durable-execution design exists
for, demonstrated by killing the process rather than by a unit test.

It also vindicates the net-P&L fix on its first live trade: **+$0.0034 gross reads as a winner,
−$0.0212 net is a loss.** Without the fix this recovery would have been recorded as profitable.

**Phase S measured the venue settle lag properly: 42.4s and 41.6s** — roughly twice the ~20s estimated
from campaign 1's coarser timing. Back-to-back cycling has to wait that out.

**Gap 5, found by Phase R — a dark research plane was disabling the operator's controls.** The preflight
set the runtime mode from *full* readiness, which includes `featuresReady` and `expertsReady`. No
strategy qualifies, so the runtime sat permanently in `EntryHalted`, and `EntryHalted` blocks manual
orders. `SystemMode` was answering two different questions at once: "is the runtime healthy enough to
act" and "is there a qualified strategy". The mode now reflects only the first. Strategy qualification
stays enforced where strategy orders are admitted — the autonomous lane still requires a ready research
plane and a forecast, `ExecutionAdmissionPolicy` still maps `QualifiedStrategy` onto full readiness, and
`ExecutionWorker` still requires an active risk reservation. Four tests pin that separating them does
not let an unqualified strategy through.

### Stress campaign 1, and the four gaps it found — 2026-09-01

21 BTC/USD round trips ran back to back. **Every one completed and reconciled `Flat`; zero failures,
zero reconciliation mismatches.** The execution path is reliable. What the campaign found was in the
reporting and the gates around it.

**Gap 1 — reported P&L was systematically optimistic, by exactly the fee.** `GrossPaperPnl` is
`(exitPrice − entryPrice) × exitQuantity`, which ignores the quantity bought and never sold. Crypto
commission is charged **in kind**, so that sliver *is* the fee. Over 20 completed trips:

| | |
| --- | --- |
| total gross | −$0.28264 |
| total net | **−$0.77379** |
| fee in kind | $0.49115 — **24.6 bps per trip** |
| winners on gross | 2 / 20 |
| winners on net | **0 / 20** |

Gross overstated the result by 174% and reported two losing trades as winners. 24.6 bps is Alpaca's
taker fee almost exactly: the cost model was right all along, the P&L reporting was wrong. This is the
most dangerous shape of error in the system, because it is the number a search would optimise against.
`NetPaperPnl` is now computed and stored alongside gross — kept side by side, because the difference
between them is the fee and is worth being able to see.

**Gap 2 — an operator could halt the system but not resume it.** `/api/system/halt` and
`/api/system/risk-reduction` exist; nothing cleared them. The preflight deliberately preserves an
operator mode so a routine cycle cannot undo a human decision, which left process restart as the only
way back. `/api/system/resume` hands the decision to the preflight rather than forcing `Ready`, so the
system resumes only if it independently reconciles — and it reports which gates are still down instead
of a bare `false`.

**Gap 3 — manual operator orders required research readiness.** `SystemMode.Ready` includes
`featuresReady` and `expertsReady`, which describe the research plane. No strategy qualifies, so `Ready`
is unreachable, so the operator's manual order path could **never** accept an order — an escape hatch
welded shut by the state of an unrelated subsystem. Manual orders are now admitted on
*infrastructure* readiness, the same bar the diagnostic lane clears to place a real order, keeping the
hard-stop modes, the risk-reduction asymmetry, the notional cap and operator-key auth.

**Gap 4 — measured, not a defect: the venue settles after we declare completion.** The lane marks
`Complete` only when its own reconciliation reads broker quantity zero, yet an independent read still
shows the position for **~20 seconds**. Two back-to-back entry attempts are refused, the third succeeds,
consistently. That is venue read consistency, and the harness — not the system — was wrong to count
those refusals as failures.

### The compiler took the first admissible spread, not the best — 2026-09-01

Following the spread measurement above. The compiler does gate on quoted spread
(`VerticalRejection.SpreadTooWide`) and searches strike pairs exhaustively, so the width filter was
never the gap. The gap was the selection:

```csharp
if (attempt.Admitted) return attempt;   // first admissible pair, in ascending strike order
```

Every quantity needed to choose well — expected payoff at the forecast price, the debit, and the
round-trip cost charge — was already computed to decide admission, then discarded. On a chain where two
verticals both qualify, the first in strike order was taken regardless of which was cheaper to enter.
With a measured 0.61% tightest against a 3.93% median spread, and a vertical crossing both legs in both
directions, that can pay several times the necessary execution cost while reporting nothing unusual.

`Prefer` now ranks admissible spreads by net expected value after costs, and `VerticalCompilation`
carries `NetExpectedValue` and `WidestLegRelativeSpread` so the choice is auditable. Refusals still rank
by how far they got, so the reported rejection remains the most informative one.

Verified by mutation: reverting to first-wins makes the compiler take a 3.20 debit where the ranked
version takes 2.20 — identical payoff at the forecast price, $100 less capital at risk. The first
attempt at this test did *not* catch the mutation, because the fixture's earlier pair failed
reward-to-risk and so only one spread was ever admissible; the fixture was rebuilt so two genuinely
compete.

### Live option spreads, measured during the session — 2026-09-01 17:40Z

The last unknown before an options trade, answered with real quotes on SPY (mid 761.91), 40
near-the-money calls inside 21 days:

| | relative spread |
| --- | --- |
| tightest | **0.61%** (`SPY260901C00749000`, 13.03/13.11) |
| median | **3.93%** |
| widest | 133% (deep OTM, penny options) |

A vertical crosses the spread on both legs in both directions, so the round-trip slippage is roughly
**twice the relative spread**: ~1.2% of premium at the tight end, ~8% at the median, before the
~$0.05/contract fees. **Only near-ATM front-expiry contracts are tradable at all** — strike selection
dominates the economics, and a compiler that picks by strike distance without checking quoted spread
will pick unsellable contracts most of the time.

Finding this required fixing the preflight, which reported a live quote as `-0 minutes old` and marked
it stale during an open market. `asOf` was captured once at the start of the run and reused, but
contract discovery pages through 5,792 contracts first, so quotes were judged against a reference time a
minute in the past. Freshness is now read at the moment of each read. Greeks still return no block from
the venue; that remains open.

### Entry attribution, and a fee that read as unexplained exposure — 2026-09-01

**Entry no longer halts on any position.** The register left this open because narrowing it needed
position attribution that did not exist. It does now: `BrokerExposureAttributor` derives claims from
each lane's durable store — orders matched exactly by deterministic client order ID, positions by symbol
against nonterminal records. Entry halts on *unattributed* exposure and abstains when the instrument it
wants is already claimed. Verified by mutation: making every position attribute fails three tests,
including the pre-existing foreign-position halt, so the narrowing did not weaken the invariant.

The honest limit, stated in the code: positions carry no client order ID, so a hand-placed position in a
symbol a lane is already trading would be absorbed rather than flagged. Everything in an unclaimed
symbol is still foreign, and a lane registered without a claim source has its exposure treated as
foreign — both failing closed.

**The interim `Mismatch` was not cosmetic.** `IsReconciled` compared the broker position to the filled
quantity exactly. Alpaca charges crypto commission **in kind**, so the position is always slightly
smaller than the fill — 0.000125468 held against 0.000125783 filled. The comparison therefore failed on
every successful crypto entry, writing `Failure = ReconciliationMismatch` and
`UNEXPLAINED_BROKER_EXPOSURE` onto a perfectly healthy record. The code two lines above already
acknowledged the fee in a comment, then compared exactly on the other branch. A shortfall within
`MaximumInKindFeeShare` (0.5%, against the 0.25% observed) is now reconciled; a position *larger* than
the fill still is not, which is the case the check exists for.

Confirmed live on `CRYPTO-DIAGNOSTIC-2026-09-01-004`: `Clean` throughout the hold, `Flat` at
completion, no failure reason, account flat afterwards.

### The deadlock was a class, not an instance — 2026-09-01

Auditing outward from the stranded position found the same mistake in three more places. All share one
shape: **a flatness or full-readiness requirement applied to the act of removing exposure.**

* **`ExecutionAdmissionPolicy` restated the readiness rule and got the exit case wrong for all three
  order classifications** — including `QualifiedStrategy => Ready`, which would have blocked a real
  strategy's exit exactly as the diagnostic lane's was blocked. It was also dead: DI-registered, tested,
  and never called by production code, so it read like a central safety gate while enforcing nothing.
  The rule now exists once, on `FullSystemReadinessSnapshot.IsReadyFor`.
* **`EntryHalted` and `RiskReductionOnly` were honoured nowhere.** Both mode names promise that closing
  still works; only `ExecutionWorker` implemented it. The operator's manual order endpoint rejected
  every order whenever the mode was not `Ready` — and the preflight enters `EntryHalted` automatically
  the instant any position exists, so holding a position locked the operator out of closing it.
* **A refused emergency close was silent.** `ClosePositionAsync` reports a broker refusal as a rejected
  result rather than an exception, and the autonomous lane discarded the return value. On the path that
  exists for when everything else has failed, a refusal looked exactly like success.

Also fixed while auditing: operator orders were submitted with `PositionIntent.Open` hardcoded, so
every manual close was journalled as an opening trade.

### A completed crypto round trip, and the deadlock it exposed — 2026-09-01

`CRYPTO-DIAGNOSTIC-2026-09-01-002` ran end to end on the paper account: entry filled 0.000125816 BTC
for $10, two-minute hold, exit filled 0.000125501, reconciliation **Flat**, gross paper P&L
**-$0.00744**. Broker confirms no positions and no open orders; readiness returned to
`brokerReconciled = true` on the next preflight cycle.

Getting there exposed a deadlock that would have stranded every position the system ever opened.

**`brokerReconciled` was defined as "the account is flat":**

```csharp
bool flatAndResolved = orders.Count == 0 && positions.All(p => p.Quantity == 0);
readiness.RecordBrokerPreflight(flatAndResolved, ...);
```

`InfrastructureExecutionReady` requires it, and `AdvanceExitAsync` required
`InfrastructureExecutionReady`. So filling an entry made the system unreconciled, which disqualified it
from running the exit that would restore reconciliation. The live position sat at `ExitDue` for five
and a half hours past its scheduled exit, refused on `INFRASTRUCTURE_NOT_READY`, with the recovery
worker cycling once a second and reporting no error the entire time.

The fix is an asymmetry the codebase already applies one layer down, where exit admission deliberately
skips the buying-power check rather than strand a position over a funding shortfall. `ExitExecutionReady`
drops only the flatness requirement; losing broker truth entirely still stops the exit, because an exit
sized against unknown state is worse than none. Three regression tests pin it.

**Two other faults found in the same run:**

* **A background probe could kill the trading API.** Eight hosted services used
  `catch (Exception) when (exception is not OperationCanceledException)`. `HttpClient` reports its own
  timeouts as `TaskCanceledException`, which *is* an `OperationCanceledException` — so the filter
  declined to catch the most common failure a polling service meets, the exception escaped
  `ExecuteAsync`, and .NET stopped the host. The API was observed dying to a research-readiness probe
  timing out. `HostedServiceFaults.IsFault` now keys on whether the stopping token was actually
  signalled.
* **`docker compose` silently overrode `.env`.** `environment:` entries take precedence over
  `env_file`, so 22 `${VAR:-default}` entries replaced operator-edited values with shell values or
  hardcoded defaults. This cost a working Alpaca credential pair (the CLI authenticated while the API
  401'd on the same repo) and then a working operator key (blank → 401 on every operator endpoint).
  Credentials now come from `env_file: .env`, and only container-specific literals remain under
  `environment:`.

### Live venue results — 2026-09-01, first authenticated run

Credentials authenticated for the first time. `capabilities`: `paperEnvironment true`,
`equityTrading true`, `cryptoTrading true`, `optionsTrading true`, **`optionsTradingLevel 3`**, no
problems. Level 3 clears the level-2 bar a debit vertical needs.

`option-preflight` against SPY, read-only, at 08:42 UTC:

| Stage | Result |
| --- | --- |
| contract discovery | **Passed — 5,792 contracts published, none excluded, 4 sampled.** Every cross-validation rule and the OCC parser fix hold against real venue data at scale. |
| latest quotes | Feed works. Real two-sided market returned (`bp 340.26 / ap 354.68`), stamped at the previous close, so stale under the 15-minute rule. **Market closed, not broken.** |
| greeks / IV | No `greeks` block on the sampled contract; same closed-market caveat. |
| historical bars | Initially `403`; **now passes with 27 bars** once the window is held behind the real-time boundary and representative strikes are sampled. |

**Corrected after direct probing: OPRA was never the blocker for historical bars.** The `403 OPRA
agreement is not signed` came from requesting bars *up to the present instant*, which is a real-time
request. The identical seven-day window returns `200` when it ends twenty minutes earlier. Verified:

| window end | result |
| --- | --- |
| now | `403 OPRA agreement is not signed` |
| now − 20 min | `200` |
| now − 2 h | `200` |

`AlpacaHistoricalOptionBarClient` now holds its window behind a twenty-minute real-time boundary and
reports the clamped end on the query, so a dataset manifest still records what was actually served.
Without this, the option dataset export would have failed for any account lacking an OPRA
subscription, for a reason unrelated to the data being asked for.

A second correction: an interim conclusion here that "the delayed tier returns no option bars" was
wrong. It came from probing deep-ITM contracts. The preflight was sampling the *first* four tradable
contracts, which are the deepest in-the-money strikes and the least liquid in the book — hence a
four-percent quoted spread and one bar in thirty days. Sampling from the middle of the chain by strike
gives **27 bars across 4 contracts**, and those are the strikes a vertical would actually use.

The run also exposed a defect in the preflight itself: it reported stale quotes as a flat failure, so
it would cry wolf on every run outside regular hours. It now reports how stale the freshest quote is
and whether the market was open, because "the feed is dead" and "the market is shut" arrive as the
same zero-usable-quotes count and call for opposite responses.

**Still unknown, and only credentials will settle it:** whether the account's option data feed is
entitled at all, whether real spreads are tight enough to clear the cost floor, and whether strike
coverage supports the vertical widths the compiler wants.

### H. Runtime verification before any trade

| # | Item |
| --- | --- |
| H1 | Rebuild and restart production containers with autonomous execution still disabled. |
| H2 | Verify API, research worker, execution and recovery workers, persistence stores, and readiness endpoints. |
| H3 | Verify the exact PAPER endpoint, authenticated account health, options and crypto permission, selected assets and contracts, and fresh market data. |
| H4 | Query broker open orders and positions; prove no unexplained exposure. |
| H5 | Prove broker/internal reconciliation passes before enabling a run. |
| H6 | Verify the chosen lane has durable automatic exit and restart recovery active. |
| H7 | Obtain a genuinely qualified, freshly published artifact under unchanged safeguards. |
| H8 | Enable exactly one bounded opportunity through the application — never a direct broker call. |
| H9 | Observe reservation, submission, fills, holding, exit, any recovery, and final reconciliation from persisted application and broker truth. |
| H10 | Report all entry/exit IDs, timestamps, prices, quantities, costs, latency, P&L, final exposure, and reconciliation. |
| H11 | Return the system to its intended safe post-run state. |

---

### Method note that will save the next session an hour

Run `dotnet restore`, `dotnet build`, and `dotnet test` inside **one** `docker run` invocation. A
throwaway container starts with an empty NuGet cache, so a `--no-build` test run against a `bin/`
produced by an earlier container silently enumerates only a subset of the test projects and still
exits 0. That is a measurement artifact, not a repository defect. CI now asserts that the number of
reported test assemblies equals the number of test projects, so the failure mode is caught rather
than believed.

## Completion definition

Completion requires current evidence proving all of the following:

1. A strategy has positive robust expectancy after realistic costs and multiplicity correction on untouched evidence.
2. The complete evidence bundle publishes atomically.
3. Runtime compiles and executes the exact tested semantics.
4. PAPER account, permission, asset/contract, risk, persistence, recovery, and reconciliation checks pass.
5. Exactly one bounded application-owned PAPER trade completes its durable managed exit.
6. Final broker and internal exposure reconcile with no unintended orders or positions.

Until all six are proven, keep the goal active and autonomous execution disabled.
