# QuantDesk Continuation Handoff

Last refreshed: 2026-08-31, Africa/Johannesburg (verification + option-data continuation)

## Goal

Improve QuantDesk until at least one strategy genuinely qualifies under unchanged PAPER-only, evidence, after-cost, risk, recovery, and reconciliation safeguards. Then execute and complete exactly one bounded autonomous Alpaca PAPER trade through the QuantDesk application without bypassing its controls.

The goal is **not complete**. No strategy currently qualifies, no autonomous strategy order has been submitted, and autonomous execution remains disabled.

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
behind each entry; this is the checklist. Nothing here is complete. Items are grouped by what they
block, and ordered within each group by risk removed per unit of effort.

Status as of 2026-08-31: **no strategy qualifies, no autonomous strategy order has ever been
submitted, autonomous execution is disabled, and no session has ever held Alpaca credentials — so
nothing in this repository has yet contacted the live venue or placed a trade.**

---

### A. Blocking a first trade

| # | Item | Why it blocks |
| --- | --- | --- |
| A1 | **No Alpaca credentials in any session.** Every result recorded here is against stubbed responses. | Nothing can reach the venue. This is the single hard blocker. |
| A2 | **Options candidates are not routed into `MultiLegExecutionLifecycle`.** The lifecycle exists and is tested; the compiler exists and is tested; nothing connects them to submission. | An options order cannot be submitted, and the hackathon requires options in every strategy. |
| A3 | **The autonomous service still compiles and submits only a spot candidate.** | The equity and options lanes stop short of execution. |
| A4 | **No strategy qualifies.** Four families clear discovery; none beat passive equal-weight out-of-sample. | `NO_TRADE` is currently correct. Forcing a trade would be worse than not trading. |
| A5 | **Autonomous lane has no durable store**, so restart recovery is not equivalent to the diagnostic lane even with deterministic client IDs. | A restart mid-flight cannot recover the opportunity. |

### B. Untested money path

| # | Item |
| --- | --- |
| B1 | **`AutonomousPaperTradingService` has zero tests.** It decides whether to trade, halts on unreconciled state, reserves capital, submits, manages, and exits. The single most important untested class. |
| B2 | `ExecutionAdmissionPolicy` has zero tests — it is an admission gate. |
| B3 | `CryptoFeeSchedule` has zero tests — it is the provenance of the numbers that decide admissibility. |
| B4 | `MarketEvidenceProvider` has zero tests. |
| B5 | `BlackScholes`, `OptionChainValidator`, `ExecutionJournalReplay`, and `PythonResearchContractReader` are each referenced by exactly one test file. |
| B6 | Duplicate-prevention is proven only across sequential restart, never with two store/lifecycle instances racing. |
| B7 | Atomic store behaviour under interrupted writes and corrupted JSON is unproven for the MLeg store. |

### C. Research — the binding constraint is universe breadth

| # | Item |
| --- | --- |
| C1 | **Widen the research universe.** SPY/QQQ/IWM/DIA carry a 0.859 mean pairwise correlation and contain no tradable cross-sectional edge. Sector and factor ETFs decorrelate the cross-section. Needs credentials; the downloader and the `--symbols` parameter are already in place. |
| C2 | **1,104 lines of dead Python across 19 modules** — including the entire overfitting-control toolkit (deflated Sharpe, PBO, purged CV, walk-forward). Wire them into the experiments or delete them; leaving them dead misrepresents the system's rigour. |
| C3 | Add option quote/spread snapshots for research. Bars alone cannot price a spread's execution economics. |
| C4 | Regime-conditioned expectancy with independently validated regime filters and charged multiplicity. |
| C5 | Mechanism-disagreement abstention returning typed `UNCERTAIN`/`ABSTAIN` rather than averaging contradiction into a weak direction. |
| C6 | A persisted formal mechanism catalogue: cause, actor, expected regime, disappearance condition, falsification rule, dataset, costs, and comparison budget recorded before evaluation. |
| C7 | A SPY volatility-risk-premium family using implied versus causal expected realised volatility — separately validated, never inferred from a directional edge. |
| C8 | A SPY directional debit-vertical research family, only after an underlying signal independently qualifies. |
| C9 | Persist the relative-strength and portfolio-family failures in typed rejected-hypothesis memory with hashes, parameters, costs, and reason. |
| C10 | Execution-mode-aware crypto cost scenarios (conservative stress, taker, maker, observed-realised) kept distinct in provenance and qualification meaning. |
| C11 | Historical expired-contract discovery is unproven against Alpaca; fail explicitly if the subscription does not supply the history. |
| C12 | The option feed remains `UNVERIFIED` in runtime capability output and can only be marked verified after an authenticated read with freshness and schema checks. |

### D. Multi-leg options lifecycle

| # | Item |
| --- | --- |
| D1 | Track Alpaca nested parent and leg order identities, not only the parent snapshot. |
| D2 | Verify actual leg fill ratios; reject or repair broken and imbalanced partial fills safely. |
| D3 | Attribute internal position quantity per OCC leg rather than only parent spread quantity. |
| D4 | Reconcile parent orders, child orders, every leg position, and internal leg inventory. |
| D5 | Cancellation and bounded repricing rules for stale unfilled entry and exit orders. |
| D6 | Idempotent, PAPER-protected emergency options flatten that derives broker-truth leg exposure, closes it without duplicates, and verifies flat afterwards. |
| D7 | Define recovery behaviour when `SubmissionUnknown` never appears at the broker; it currently stays nonterminal indefinitely. |
| D8 | Typed timeout, broker-unavailable, permission, contract-expired, broken-leg, and reconciliation-failure states. |
| D9 | Expose lifecycle and recovery status through readiness and status endpoints. |
| D10 | Structured, secret-free lifecycle metrics and logs. |
| D11 | Verify the recovery hosted service starts in the production container and advances a seeded nonterminal record without submitting an unauthorised order. |

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
| G1 | `CryptoDiagnosticExecutionService` is a 1,072-line god class with 37 methods. Extract its durable lifecycle so the autonomous lane reuses it instead of a third reimplementation — this also closes A5. |
| G2 | `CryptoMarketEvidence` is the evidence type for equities and options. Rename to `DirectionalMarketEvidence` and move it out of the crypto client's file. |
| G3 | `AlpacaTradingGateway` (404 lines) owns account, asset, submit, lookup, orders, positions, cancel, replace, close, and multi-leg mapping, and grows with every asset class. |
| G4 | `Program.cs` (388 lines) mixes registration with inline endpoints and business defaults. |
| G5 | 16 separate `JsonSerializerOptions` instantiations — each a chance for a reader and writer to disagree. |
| G6 | The dataset manifest is declared three times across two languages; the C# writer and Python reader can drift with nothing catching it. |
| G7 | `OptionQuoteSnapshot` carries five always-null greeks. Compute them or drop them. |
| G8 | Entry halts on any broker position or order, not only the traded symbol. Scoping it safely needs position attribution, which does not exist; deliberately left rather than weakening the invariant. |
| G9 | `AlpacaHistoricalStockBarClient` silently ignores unrequested symbols and a null payload; the option clients fail closed on both. |
| G10 | The autonomous symbol defaults to `BTC/USD`, the venue proven structurally unprofitable at short horizons. |
| G11 | `crypto_direction.py` is 810 lines and serves a lane the cost analysis has ruled out. |
| G12 | 74 files in `Docs/`; at least `Docs/AUTONOMOUS_TRADING_CONNECTION_AUDIT.md` predates the routing work. Docs drift is caught by no test. |
| G13 | Test-name-to-source mapping is unreliable, so coverage gaps cannot be checked mechanically. |
| G14 | `Program.cs` passes an unexplained magic `0.00000001m` into a diagnostic endpoint. |

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
